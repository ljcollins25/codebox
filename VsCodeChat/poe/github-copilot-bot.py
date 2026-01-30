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
    introduction_message="I'm a GitHub Copilot-compatible API bot. Configure your API settings using the parameter controls, or leave the API key blank to authenticate via GitHub.",
    parameter_controls=ParameterControls(
        sections=[
            Section(
                name="Connection",
                controls=[
                    TextField(
                        label="API Key",
                        parameter_name="api_key",
                        description="Your API key (leave blank to auto-authenticate via GitHub)",
                        placeholder="sk-... (optional)",
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
                            ValueNamePair(name="Gemini 3 Flash (Preview)", value="gemini-3-flash-preview"),
                            ValueNamePair(name="Gemini 3 Pro (Preview)", value="gemini-3-pro-preview"),
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
    def _try_extract_token(self, response) -> str | None:
        """Try to extract token from response. Returns token string or None."""
        try:
            result = json.loads(response.text)
            if result.get("status") == "acquired":
                return result.get("copilot_token")
        except json.JSONDecodeError:
            pass
        return None

    def _get_token(self) -> str | None:
        """Get Copilot token from VSC-CPT. Returns token string or None."""
        # Initial "fast" check
        response = poe.call("VSC-CPT", "{ mode: query_token }")
        token = self._try_extract_token(response)
        if token:
            return token

        # Run auth flow with progress shown to user
        poe.call("VSC-CPT", "{ mode: auth_flow, poll_interval_secs: 5, poll_count: 30 }", output=poe.default_chat)

        # Get the token after auth flow
        response = poe.call("VSC-CPT", "{ mode: query_token }")
        return self._try_extract_token(response)

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
        api_key = get_param("api_key")
        model = get_param("model", "gpt-4o")

        # Use custom model if selected
        if model == "custom":
            model = get_param("custom_model", "gpt-4o")

        # If no API key provided, try to get one from VSC-CPT
        if not api_key:
            api_key = self._get_token()
            if not api_key:
                raise poe.BotError("Could not authenticate with GitHub Copilot. Please provide an API key or complete the GitHub authentication flow.")

        # Determine model type (matching PowerShell logic)
        is_codex_model = "codex" in model.lower()
        is_internal_model = model.startswith("copilot-")

        # Get proxy endpoint from token
        proxy_ep = get_proxy_endpoint(api_key)

        # Determine base host based on model type (matching PowerShell logic exactly)
        if is_internal_model:
            # Internal copilot- models need the enterprise proxy endpoint
            if proxy_ep:
                base_host = f"https://{proxy_ep}" if not proxy_ep.startswith("http") else proxy_ep
            else:
                base_host = "https://copilot-proxy.githubusercontent.com"
        else:
            # Standard models (gpt-4o, claude-sonnet-4, gemini-3-pro, etc.) use the main API
            base_host = "https://api.githubcopilot.com"

        # Determine endpoint based on model type (matching PowerShell logic exactly)
        if is_codex_model:
            # Codex models use /responses endpoint
            api_url = f"{base_host}/responses"
            is_responses_endpoint = True
            is_chat_endpoint = False
        else:
            # Non-codex models use /chat/completions
            api_url = f"{base_host}/chat/completions"
            is_responses_endpoint = False
            is_chat_endpoint = True

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
            "Editor-Version": "vscode/1.104.1",
            "Editor-Plugin-Version": "copilot-chat/0.36.0",
            "Openai-Organization": "github-copilot",
            "Copilot-Integration-Id": "vscode-chat",
        }

        if is_responses_endpoint:
            # /responses endpoint uses {model, input, stream} format
            input_text = messages_to_prompt(messages)
            payload = {
                "model": model,
                "input": input_text,
                "stream": True,
            }
        else:
            # /chat/completions endpoint
            payload = {
                "model": model,
                "messages": messages,
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

                        current_event = ""
                        for line in response.iter_lines():
                            # Track event type for /responses endpoint
                            if line.startswith("event: "):
                                current_event = line[7:]
                                continue

                            if line.startswith("data: "):
                                data = line[6:]
                                if data == "[DONE]":
                                    break
                                try:
                                    chunk = json.loads(data)

                                    if is_responses_endpoint:
                                        # /responses endpoint: look for response.output_text.delta events
                                        if current_event == "response.output_text.delta" and "delta" in chunk:
                                            content = chunk.get("delta", "")
                                            if content:
                                                output_msg.write(content)
                                    else:
                                        # /chat/completions endpoint
                                        choices = chunk.get("choices", [])
                                        if choices:
                                            delta = choices[0].get("delta", {})
                                            content = delta.get("content", "")
                                            if content:
                                                output_msg.write(content)
                                except json.JSONDecodeError:
                                    continue
            except httpx.RequestError as e:
                raise poe.BotError(f"Connection error: {str(e)}")


if __name__ == "__main__":
    bot = OpenAIAPIBot()
    bot.run()