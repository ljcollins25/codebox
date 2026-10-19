"""Local stand-in for the `poe` global available to Poe script bots.

Follows https://creator.poe.com/docs/script-bots/poe-python-reference (checked 2026-10-19).
See README "Runner gaps" for what is stubbed or missing.
"""
import contextlib
import json
import mimetypes
import os
import threading
import urllib.request
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path


class BotError(Exception):
    """Error to show to the user."""

    def __init__(self, message=""):
        super().__init__(message)
        self.message = message


class Sender:
    def __init__(self, role="user", id=None, name=None):
        self.role, self.id, self.name = role, id, name


class UserMember:
    def __init__(self, name=None, user_identifier="", photo_url=""):
        self.name, self.user_identifier, self.photo_url = name, user_identifier, photo_url


class Attachment:
    """Attachment from bytes or a URL. Output attachments get a file:// URL in the out dir
    (real Poe re-hosts them on its own storage)."""

    def __init__(self, name, contents=None, url=None, content_type=None, is_inline=False, _runtime=None):
        if (contents is None) == (url is None):
            raise ValueError("Specify exactly one of contents or url")
        self._name = name
        self._contents = contents
        self._src_url = url
        self._content_type = content_type or mimetypes.guess_type(name)[0] or "application/octet-stream"
        self.is_inline = is_inline
        self._runtime = _runtime
        self._url = url if (url and url.startswith("http://127.0.0.1")) else None

    name = property(lambda s: s._name)
    content_type = property(lambda s: s._content_type)

    @property
    def url(self):
        if self._url is None:
            rt = self._runtime or _runtime
            self._url = rt.host_attachment(self)
        return self._url

    @property
    def inline_ref(self):
        if not self.is_inline:
            return None
        return "![%s][%s]" % (self.name, self.name)

    def get_contents(self):
        if self._contents is None:
            with urllib.request.urlopen(self._src_url, timeout=60) as r:
                self._contents = r.read()
        return self._contents

    def copy(self, name=None, is_inline=None):
        return Attachment(name or self.name, contents=self.get_contents(), content_type=self.content_type,
                          is_inline=self.is_inline if is_inline is None else is_inline)

    def __add__(self, other):
        return Message(text=other, attachments=[self]) if isinstance(other, str) else Message(text=other.text, attachments=[self, *other.attachments])

    def __radd__(self, other):
        return Message(text=other, attachments=[self]) if isinstance(other, str) else Message(text=other.text, attachments=[*other.attachments, self])


class Message:
    def __init__(self, text="", sender="user", content_type="text/markdown", attachments=None,
                 in_progress=False, is_tool_call=False, parameters=None):
        self._text = text
        self.sender = sender if isinstance(sender, Sender) else Sender(role=sender)
        self.content_type = content_type
        self._attachments = list(attachments or [])
        self._in_progress = in_progress
        self._finished = False
        self.is_tool_call = is_tool_call
        self.parameters = parameters if parameters is not None else {}
        self.failed = None

    text = property(lambda s: s._text)
    role = property(lambda s: s.sender.role)
    attachments = property(lambda s: tuple(s._attachments))

    def _check(self):
        if not self._in_progress or self._finished:
            raise ValueError("message is not in progress")

    def write(self, text):
        self._check()
        self._text += text

    def overwrite(self, text):
        self._check()
        self._text = text

    def fail(self, error_text):
        self._check()
        self.failed = error_text

    def add_attachment(self, attachment, add_inline_ref=True):
        self._check()
        self._attachments.append(attachment)
        if add_inline_ref and self.content_type == "text/markdown" and attachment.is_inline:
            self._text += attachment.inline_ref

    def add_inline_ref(self, attachment):
        """Not in the official reference as a Message method; accepted for convenience."""
        self._check()
        if attachment.inline_ref:
            self._text += attachment.inline_ref

    def attach_file(self, name, contents, content_type=None, is_inline=False):
        self._check()
        a = Attachment(name=name, contents=contents, content_type=content_type, is_inline=is_inline)
        self.add_attachment(a)
        return a

    def update(self, part):
        self._check()
        if getattr(part, "is_replace_response", False):
            self.overwrite(getattr(part, "text", ""))
        elif getattr(part, "text", None):
            self.write(part.text)
        a = getattr(part, "attachment", None)
        if a is not None:
            self.add_attachment(a)

    def finish(self):
        self._check()
        self._finished = True

    def copy(self, text=None, sender=None, attachments=None, parameters=None):
        if self._in_progress and not self._finished:
            raise ValueError("cannot copy in-progress message")
        return Message(self._text if text is None else text, self.sender if sender is None else sender,
                       self.content_type, self._attachments if attachments is None else attachments,
                       parameters=dict(self.parameters if parameters is None else parameters))

    def __add__(self, other):
        if isinstance(other, str):
            return self.copy(text=self._text + other)
        if isinstance(other, Attachment):
            return self.copy(attachments=[*self._attachments, other])
        if isinstance(other, (list, tuple)):
            return self.copy(attachments=[*self._attachments, *other])
        return Message(self._text + other.text, other.sender, other.content_type,
                       [*self._attachments, *other.attachments], parameters={**self.parameters, **other.parameters})

    def __radd__(self, other):
        return Message(str(other) + self._text, self.sender, self.content_type, self._attachments, parameters=self.parameters)

    def __enter__(self):
        return self

    def __exit__(self, et, ev, tb):
        if self._in_progress and not self._finished:
            self._finished = True
        return False

    def __contains__(self, item):
        return item in self._text

    def __format__(self, spec):
        return format(self._text, spec)

    def __str__(self):
        return self._text


class Chat:
    def __init__(self, *args, initial_length=None, quiet=True, user_members=None, _runtime=None):
        self.messages = []
        for a in args:
            if isinstance(a, Chat):
                self.messages.extend(a.messages)
            elif isinstance(a, str):
                self.messages.append(Message(text=a))
            else:
                self.messages.append(a)
        self.initial_length = len(self.messages) if initial_length is None else initial_length
        self.quiet = quiet
        self.user_members = user_members or []
        self._runtime = _runtime

    last = property(lambda s: s.messages[-1])
    text = property(lambda s: "\n".join(m.text for m in s.messages))

    def __len__(self):
        return len(self.messages)

    def __getitem__(self, i):
        return self.messages[i]

    def add_message(self, message):
        if isinstance(message, str):
            message = Message(text=message)
        self.messages.append(message)

    def add_messages(self, messages):
        for m in messages:
            self.add_message(m)

    def start_message(self, sender=None, content_type="text/markdown"):
        m = Message(text="", sender=sender or Sender("bot", name=_runtime.bot_name if _runtime else None),
                    content_type=content_type, in_progress=True)
        self.add_message(m)
        return m

    def new_messages(self):
        return self.messages[self.initial_length:]

    def make_child(self):
        return Chat(self, initial_length=len(self.messages), quiet=self.quiet)

    def copy(self, quiet=None):
        return Chat(self, initial_length=self.initial_length, quiet=self.quiet if quiet is None else quiet)

    def summarize(self):
        return "\n".join("%s: %s" % (m.role, m.text[:80]) for m in self.messages)

    def __iadd__(self, other):
        if isinstance(other, (str, Message)):
            self.add_message(other)
        else:
            self.add_messages(other)
        return self

    def __contains__(self, item):
        if isinstance(item, str):
            return any(item in m.text for m in self.messages)
        return any(m.text == item.text for m in self.messages)

    def __enter__(self):
        global default_chat
        self._prev = default_chat
        default_chat = self
        return self

    def __exit__(self, *a):
        global default_chat
        default_chat = self._prev
        return False

    def __format__(self, spec):
        return format(self.text, spec)


class Doc:
    def __class_getitem__(cls, item):
        return item[0] if isinstance(item, tuple) else item


class _AttachServer:
    """Serves --attach files on 127.0.0.1 so bots that fetch attachment.url over HTTP work."""

    def __init__(self):
        self.files = {}
        files = self.files

        class H(BaseHTTPRequestHandler):
            def do_GET(self):
                p = files.get(self.path.lstrip("/").split("?")[0])
                if not p:
                    self.send_response(404); self.end_headers(); return
                data = Path(p).read_bytes()
                self.send_response(200)
                self.send_header("Content-Type", mimetypes.guess_type(p)[0] or "application/octet-stream")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            def log_message(self, *a):
                pass

        self.srv = HTTPServer(("127.0.0.1", 0), H)
        self.port = self.srv.server_address[1]
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()

    def add(self, path):
        name = Path(path).name
        self.files[name] = str(path)
        return "http://127.0.0.1:%d/%s" % (self.port, name)

    def close(self):
        self.srv.shutdown(); self.srv.server_close()


class Runtime:
    """Holds one run's state: query, messages, settings, output directory."""

    def __init__(self, text="", attachments=(), parameters=None, out_dir="out", bot_name="local-bot", api_key=None):
        self.bot_name = bot_name
        self.out_dir = Path(out_dir)
        self.settings = None
        self.api_key = api_key if api_key is not None else os.environ.get("POE_API_KEY")
        self.user_parameters = dict(parameters or {})
        self._server = None
        self.saved = {}
        atts = []
        for p in attachments:
            if self._server is None:
                self._server = _AttachServer()
            url = self._server.add(p)
            atts.append(Attachment(Path(p).name, url=url, content_type=mimetypes.guess_type(str(p))[0], _runtime=self))
        self.query = Message(text=text, sender="user", attachments=atts, parameters=dict(self.user_parameters))
        self.default_chat = Chat(self.query, quiet=False)
        self.default_chat.initial_length = 1
        self.default_chat._runtime = self

    def host_attachment(self, att):
        self.out_dir.mkdir(parents=True, exist_ok=True)
        path = self.out_dir / att.name
        n = 1
        while path.exists() and path.name in self.saved and self.saved[path.name] is not att:
            n += 1
            path = self.out_dir / ("%d_%s" % (n, att.name))
        path.write_bytes(att.get_contents())
        self.saved[path.name] = att
        return path.resolve().as_uri()

    def apply_defaults(self, settings):
        """Real Poe pre-fills parameters with the controls' default_value."""
        pc = getattr(settings, "parameter_controls", None)
        for sec in (getattr(pc, "sections", None) or []):
            for c in (getattr(sec, "controls", None) or []):
                pn, dv = getattr(c, "parameter_name", None), getattr(c, "default_value", None)
                if pn is not None and dv is not None:
                    self.query.parameters.setdefault(pn, dv)

    def update_settings(self, settings):
        self.settings = settings
        self.apply_defaults(settings)

    def messages(self):
        return [m for m in self.default_chat.messages[1:] if m.role == "bot"]

    def close(self):
        if self._server:
            self._server.close()

    def finalize(self):
        """Make sure every attachment of every bot message is written to out/."""
        for m in self.messages():
            for a in m.attachments:
                a.url


_runtime = None
default_chat = None
bot_name = "local-bot"
query = None


def _bind(rt):
    global _runtime, default_chat, bot_name, query
    _runtime, default_chat, bot_name, query = rt, rt.default_chat, rt.bot_name, rt.query


def start_message(sender=None, content_type="text/markdown"):
    return default_chat.start_message(sender=sender, content_type=content_type)


def update_settings(settings):
    _runtime.update_settings(settings)


@contextlib.contextmanager
def set_query(q):
    global query
    prev = query
    query = q if isinstance(q, Message) else Message(text=q)
    try:
        yield query
    finally:
        query = prev


def _to_openai(prompts):
    out = []
    for p in prompts:
        if isinstance(p, str):
            out.append({"role": "user", "content": p})
        elif isinstance(p, Chat):
            out += [{"role": "assistant" if m.role == "bot" else m.role, "content": m.text} for m in p.messages]
        else:
            out.append({"role": "assistant" if p.role == "bot" else p.role, "content": p.text})
    return out


def stream(bot_name_, *prompts, **kw):
    """Real call through the Poe OpenAI-compatible API when POE_API_KEY is set; otherwise a stub."""
    name = bot_name_.lstrip("@")
    key = _runtime.api_key
    if not key:
        yield type("PartialResponse", (), {"text": "[poe.call stub: %s; set POE_API_KEY for a real call]" % name,
                                           "is_replace_response": False, "attachment": None})()
        return
    body = {"model": name, "messages": _to_openai(prompts)}
    if kw.get("temperature") is not None:
        body["temperature"] = kw["temperature"]
    req = urllib.request.Request("https://api.poe.com/v1/chat/completions", json.dumps(body).encode(),
                                 {"Authorization": "Bearer " + key, "Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=120) as r:
        data = json.load(r)
    yield type("PartialResponse", (), {"text": data["choices"][0]["message"]["content"],
                                       "is_replace_response": False, "attachment": None})()


def call(bot_name_, *prompts, output=None, **kw):
    chat = output if output is not None else Chat(quiet=True)
    with chat.start_message() as m:
        for part in stream(bot_name_, *prompts, **kw):
            m.write(part.text)
    return chat


def parallel(*tasks, return_exceptions=False, skip_exceptions=False):
    res = []
    for t in tasks:
        try:
            res.append(t())
        except BaseException as e:  # noqa: BLE001
            if skip_exceptions:
                continue
            if return_exceptions:
                res.append(e)
            else:
                raise
    return res


def repeat(n, func):
    return parallel(*([func] * n), skip_exceptions=True)


def call_tools(*a, **k):
    raise NotImplementedError("poe.call_tools is not implemented in the local runner")


def update_tool_calls(*a, **k):
    raise NotImplementedError("poe.update_tool_calls is not implemented in the local runner")
