import os
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "runner"))
import run_bot  # noqa: E402
import poe_stub  # noqa: E402


class RunnerTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.out = self.tmp / "out"

    def bot(self, code):
        p = self.tmp / "bot.py"
        p.write_text(textwrap.dedent(code), encoding="utf-8")
        return p

    def run_it(self, code, **kw):
        os.environ.pop("POE_API_KEY", None)
        return run_bot.run(self.bot(code), out_dir=self.out, **kw)

    def test_write_and_query_text(self):
        msgs, rt, err = self.run_it("""
            with poe.start_message() as m:
                m.write("echo: " + poe.query.text)
        """, text="hi")
        self.assertIsNone(err)
        self.assertEqual([m.text for m in msgs], ["echo: hi"])

    def test_print_posts_message(self):
        msgs, _, _ = self.run_it("print('a', 'b')")
        self.assertEqual(msgs[0].text, "a b")

    def test_bot_error(self):
        msgs, _, err = self.run_it("raise poe.BotError('nope')")
        self.assertEqual(err, "nope")

    def test_parameters_defaults_and_override(self):
        msgs, _, _ = self.run_it("""
            from fastapi_poe.types import SettingsResponse, ParameterControls, Section, TextArea
            poe.update_settings(SettingsResponse(introduction_message="x", parameter_controls=ParameterControls(
                sections=[Section(name="s", controls=[
                    TextArea(label="a", parameter_name="a", default_value="da"),
                    TextArea(label="b", parameter_name="b", default_value="db")])])))
            print(poe.query.parameters["a"], poe.query.parameters["b"])
        """, params={"b": "over"})
        self.assertEqual(msgs[0].text, "da over")

    def test_attachment_in_and_out(self):
        src = self.tmp / "in.txt"
        src.write_text("hello")
        msgs, _, _ = self.run_it("""
            a = poe.query.attachments[0]
            data = a.get_contents()
            with poe.start_message() as m:
                m.write(a.name)
                m.attach_file("copy.txt", data.upper(), content_type="text/plain")
        """, attachments=[src])
        self.assertEqual(msgs[0].text, "in.txt")
        self.assertEqual((self.out / "copy.txt").read_bytes(), b"HELLO")

    def test_attachment_url_http_for_inputs(self):
        src = self.tmp / "in.txt"
        src.write_text("hello")
        msgs, _, _ = self.run_it("""
            import urllib.request
            print(urllib.request.urlopen(poe.query.attachments[0].url).read().decode())
        """, attachments=[src])
        self.assertEqual(msgs[0].text, "hello")

    def test_inline_ref(self):
        msgs, _, _ = self.run_it("""
            with poe.start_message() as m:
                a = poe.Attachment("p.png", contents=b"x", is_inline=True)
                m.add_attachment(a)
        """)
        self.assertIn("![p.png]", msgs[0].text)
        msgs, _, _ = self.run_it("""
            with poe.start_message() as m:
                a = poe.Attachment("p.png", contents=b"x", is_inline=True)
                m.add_attachment(a, add_inline_ref=False)
        """)
        self.assertEqual(msgs[0].text, "")
        self.assertTrue((self.out / "p.png").exists())

    def test_write_after_finish_raises(self):
        with self.assertRaises(ValueError):
            m = poe_stub.Message("", in_progress=True)
            m.finish()
            m.write("x")

    def test_call_stub_without_key(self):
        msgs, _, _ = self.run_it("print(poe.call('GPT-4o', 'hi').last.text)")
        self.assertIn("stub", msgs[0].text)

    def test_update_settings_recorded_and_main_guard(self):
        _, rt, _ = self.run_it("""
            from fastapi_poe.types import SettingsResponse
            poe.update_settings(SettingsResponse(introduction_message="hello"))
        """)
        self.assertEqual(rt.settings.introduction_message, "hello")

    def test_cli_param_parsing(self):
        self.assertEqual(run_bot.parse_param("n=5"), ("n", 5))
        self.assertEqual(run_bot.parse_param("a=-t 5 -c copy"), ("a", "-t 5 -c copy"))


if __name__ == "__main__":
    unittest.main()
