"""Minimal stand-in for fastapi_poe.types, used only when fastapi_poe is not installed.
Every type is a permissive keyword-argument container; it does not validate."""
import sys, types


class _Obj:
    def __init__(self, *args, **kwargs):
        self._args = args
        self.__dict__.update(kwargs)

    def __repr__(self):
        return "%s(%s)" % (type(self).__name__, ", ".join("%s=%r" % kv for kv in self.__dict__.items() if kv[0] != "_args"))


_NAMES = ["SettingsResponse", "ParameterControls", "Section", "TextArea", "DropDown", "ValueNamePair",
          "Slider", "Toggle", "TextField", "Divider", "ProtocolMessage", "PartialResponse", "Attachment",
          "ToolDefinition", "ToolCallDefinition", "ToolResultDefinition", "ToolCallDefinitionDelta",
          "ParameterResponse", "ContentType", "MetaResponse", "ErrorResponse", "Button", "ConditionallyRenderedControl"]


def install():
    try:
        import fastapi_poe  # noqa: F401
        return False
    except ImportError:
        pass
    pkg = types.ModuleType("fastapi_poe")
    typ = types.ModuleType("fastapi_poe.types")
    for n in _NAMES:
        c = type(n, (_Obj,), {})
        setattr(typ, n, c)
        setattr(pkg, n, c)
    pkg.types = typ
    pkg.__shim__ = True
    sys.modules["fastapi_poe"] = pkg
    sys.modules["fastapi_poe.types"] = typ
    return True
