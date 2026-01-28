# poe: name=Github-Copilot-Bot
# poe: privacy_shield=half

import json
import httpx
from fastapi_poe.types import (
    SettingsResponse,
    ParameterControls,
    Section,
    Slider,
    TextField,
    DropDown,
    ValueNamePair,
)

# Configure bot settings with parameter controls
poe.update_settings(SettingsResponse(
    introduction_message="I'm a GitHub Copilot-compatible API bot. Configure your API settings using the parameter controls.",
    parameter_controls=ParameterControls(
        sections=[
            Section(
                name="Connection",
                controls=[
                    TextField(
                        label="API Key",
                        parameter_name="api_key",
                        description="Your API key (required)",
                        placeholder="sk-...",
                    ),
                    TextField(
                        label="API URL",
                        parameter_name="api_url",
                        description="OpenAI-compatible chat completions endpoint",
                        default_value="https://api.githubcopilot.com/chat/completions",
                        placeholder="https://api.githubcopilot.com/chat/completions",
                    ),
                    DropDown(
                        label="Model",
                        parameter_name="model",
                        description="Model to use",
                        default_value="gpt-4o",
                        options=[
                            ValueNamePair(name="GPT-4.1", value="gpt-4.1"),
                            ValueNamePair(name="GPT-4o", value="gpt-4o"),
                            ValueNamePair(name="GPT-5 mini", value="gpt-5-mini"),
                            ValueNamePair(name="Claude Haiku 4.5", value="claude-haiku-4.5"),
                            ValueNamePair(name="Claude Opus 4.5", value="claude-opus-4.5"),
                            ValueNamePair(name="Claude Sonnet 4", value="claude-sonnet-4"),
                            ValueNamePair(name="Claude Sonnet 4.5", value="claude-sonnet-4.5"),
                            ValueNamePair(name="Gemini 2.5 Pro", value="gemini-2.5-pro"),
                            ValueNamePair(name="Gemini 3 Flash (Preview)", value="gemini-3-flash"),
                            ValueNamePair(name="Gemini 3 Pro (Preview)", value="gemini-3-pro"),
                            ValueNamePair(name="GPT-5", value="gpt-5"),
                            ValueNamePair(name="GPT-5-Codex (Preview)", value="gpt-5-codex"),
                            ValueNamePair(name="GPT-5.1", value="gpt-5.1"),
                            ValueNamePair(name="GPT-5.1-Codex", value="gpt-5.1-codex"),
                            ValueNamePair(name="GPT-5.1-Codex-Max", value="gpt-5.1-codex-max"),
                            ValueNamePair(name="GPT-5.1-Codex-Mini (Preview)", value="gpt-5.1-codex-mini"),
                            ValueNamePair(name="GPT-5.2", value="gpt-5.2"),
                            ValueNamePair(name="GPT-5.2-Codex", value="gpt-5.2-codex"),
                            ValueNamePair(name="Custom...", value="custom"),
                        ],
                    ),
                    TextField(
                        label="Custom Model",
                        parameter_name="custom_model",
                        description="Used when 'Custom...' is selected above",
                        placeholder="gpt-4-turbo",
                    ),
                ],
            ),
            Section(
                name="Model Parameters",
                controls=[
                    Slider(
                        label="Temperature",
                        parameter_name="temperature",
                        description="Controls randomness (0 = deterministic, 2 = creative)",
                        default_value=1.0,
                        min_value=0.0,
                        max_value=2.0,
                        step=0.1,
                    ),
                    Slider(
                        label="Max Tokens",
                        parameter_name="max_tokens",
                        description="Maximum tokens in response (0 = no limit)",
                        default_value=0,
                        min_value=0,
                        max_value=16384,
                        step=256,
                    ),
                    Slider(
                        label="Top P",
                        parameter_name="top_p",
                        description="Nucleus sampling threshold",
                        default_value=1.0,
                        min_value=0.0,
                        max_value=1.0,
                        step=0.05,
                    ),
                    Slider(
                        label="Frequency Penalty",
                        parameter_name="frequency_penalty",
                        description="Penalizes repeated tokens based on frequency",
                        default_value=0.0,
                        min_value=-2.0,
                        max_value=2.0,
                        step=0.1,
                    ),
                    Slider(
                        label="Presence Penalty",
                        parameter_name="presence_penalty",
                        description="Penalizes tokens based on presence in text",
                        default_value=0.0,
                        min_value=-2.0,
                        max_value=2.0,
                        step=0.1,
                    ),
                ],
            ),
        ]
    ),
))


class OpenAIAPIBot:
    def run(self):
        # Get parameters from the query
        params = poe.query.parameters or {}

        # Helper function to get and trim string parameters
        def get_param(key, default=None):
            value = params.get(key, default)
            if isinstance(value, str):
                return value.strip()
            return value

        def messages_to_prompt(message_list):
            lines = []
            for msg in message_list:
                role = msg.get("role", "user")
                content = msg.get("content", "")
                lines.append(f"{role}: {content}")
            lines.append("assistant:")
            return "\n".join(lines)

        def get_proxy_endpoint(token_value):
            if not token_value:
                return None
            marker = "proxy-ep="
            start = token_value.find(marker)
            if start == -1:
                return None
            start += len(marker)
            end = token_value.find(";", start)
            if end == -1:
                end = len(token_value)
            return token_value[start:end]

        # Connection parameters
        api_url = get_param("api_url", "https://api.githubcopilot.com/chat/completions")
        api_key = get_param("api_key")
        model = get_param("model", "gpt-4o")
        
        # Use custom model if selected
        if model == "custom":
            model = get_param("custom_model", "gpt-4o")

        proxy_ep = get_proxy_endpoint(api_key)
        if proxy_ep and "api.githubcopilot.com" in api_url:
            api_url = api_url.replace("api.githubcopilot.com", proxy_ep)

        normalized_url = api_url.rstrip("/")
        is_chat_endpoint = normalized_url.endswith("/chat/completions") or normalized_url.endswith("/v1/chat/completions")
        is_codex_model = "codex" in model

        if is_codex_model and is_chat_endpoint:
            if normalized_url.endswith("/v1/chat/completions"):
                base_url = normalized_url[: -len("/v1/chat/completions")]
            else:
                base_url = normalized_url[: -len("/chat/completions")]
            api_url = f"{base_url}/completions"
            is_chat_endpoint = False

        if not api_key:
            raise poe.BotError("Please provide an 'api_key' parameter to use this bot.")

        # Model parameters (use defaults that match slider defaults)
        temperature = params.get("temperature", 1.0)
        max_tokens = params.get("max_tokens", 0)
        top_p = params.get("top_p", 1.0)
        frequency_penalty = params.get("frequency_penalty", 0.0)
        presence_penalty = params.get("presence_penalty", 0.0)

        # Build messages from conversation history and current query
        messages = []

        # Add conversation history
        for msg in poe.default_chat:
            role = msg.sender.role
            if role == "user":
                messages.append({"role": "user", "content": msg.text})
            elif role == "bot":
                messages.append({"role": "assistant", "content": msg.text})
            elif role == "system":
                messages.append({"role": "system", "content": msg.text})

        # Add current query
        messages.append({"role": "user", "content": poe.query.text})

        # Prepare the API request (matching VSCode Copilot headers)
        headers = {
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json",
            "Accept": "application/json",
            "User-Agent": "GitHubCopilotChat/1.0.0",
            "Editor-Version": "vscode/1.85.0",
            "Editor-Plugin-Version": "copilot-chat/1.0.0",
            "Openai-Organization": "github-copilot",
            "Copilot-Integration-Id": "vscode-chat",
        }

        if is_chat_endpoint:
            payload = {
                "model": model,
                "messages": messages,
                "stream": True,
                "temperature": float(temperature),
                "top_p": float(top_p),
                "frequency_penalty": float(frequency_penalty),
                "presence_penalty": float(presence_penalty),
            }
        else:
            prompt = messages_to_prompt(messages)
            payload = {
                "model": model,
                "prompt": prompt,
                "stream": True,
                "temperature": float(temperature),
                "top_p": float(top_p),
                "frequency_penalty": float(frequency_penalty),
                "presence_penalty": float(presence_penalty),
            }

        # Add max_tokens only if non-zero (0 means no limit)
        if max_tokens and int(max_tokens) > 0:
            payload["max_tokens"] = int(max_tokens)

        # Make streaming request to the GitHub Copilot-compatible API
        with poe.start_message() as output_msg:
            try:
                with httpx.Client(timeout=120.0) as client:
                    with client.stream("POST", api_url, headers=headers, json=payload) as response:
                        if response.status_code != 200:
                            error_text = response.read().decode()
                            raise poe.BotError(f"API error ({response.status_code}): {error_text}")

                        for line in response.iter_lines():
                            if line.startswith("data: "):
                                data = line[6:]
                                if data == "[DONE]":
                                    break
                                try:
                                    chunk = json.loads(data)
                                    choices = chunk.get("choices", [])
                                    if choices:
                                        if is_chat_endpoint:
                                            delta = choices[0].get("delta", {})
                                            content = delta.get("content", "")
                                        else:
                                            content = choices[0].get("text", "")
                                        if content:
                                            output_msg.write(content)
                                except json.JSONDecodeError:
                                    continue
            except httpx.RequestError as e:
                raise poe.BotError(f"Connection error: {str(e)}")


if __name__ == "__main__":
    bot = OpenAIAPIBot()
    bot.run()
