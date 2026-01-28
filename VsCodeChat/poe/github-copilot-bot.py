# poe: name=OpenAI-API-Bot
# poe: privacy_shield=half

import json
import httpx
from fastapi_poe.types import (
    SettingsResponse,
    ParameterControls,
    Section,
    Slider,
    TextField,
)

# Configure bot settings with parameter controls
poe.update_settings(SettingsResponse(
    introduction_message="I'm an OpenAI-compatible API bot. Configure your API settings using the parameter controls.",
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
                        default_value="https://api.openai.com/v1/chat/completions",
                        placeholder="https://api.openai.com/v1/chat/completions",
                    ),
                    TextField(
                        label="Model",
                        parameter_name="model",
                        description="Model name to use",
                        default_value="gpt-4o",
                        placeholder="gpt-4o",
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

        # Connection parameters
        api_url = get_param("api_url", "https://api.openai.com/v1/chat/completions")
        api_key = get_param("api_key")
        model = get_param("model", "gpt-4o")

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

        # Prepare the API request
        headers = {
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json"
        }

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

        # Make streaming request to the OpenAI-compatible API
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
                                    delta = chunk.get("choices", [{}])[0].get("delta", {})
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
