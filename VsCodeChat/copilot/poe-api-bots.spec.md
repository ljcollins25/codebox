See Overview:
https://creator.poe.com/docs/api-bots/overview

Write a script with arguments which takes model files and create/updates api bots. No pricing is specified for now.

- BaseUrl: Base Url (used for ```base_url``` field)
- PoeKey: (required) Poe Api Key
- ApiKey: (required) Api key for backing endpoint specified by BaseUrl (used for ```api_key``` field)
- Handle: (optional) Tokenized value for ```handle``` field. Supports tokens replaced with fields from model json: {name}, {id}, {version}. (Default value is "VSC-{id}")
- Description: (optional) Tokenized value for ```description``` field. (Default is "VSC {name} bot") Accepts tokens like Handle.
- Path: File or folder with model files (from OpenAI models)
- Public: (optional default is false) Switch to enable publishing model(s) ```"is_private": false```.
- Publish: (optional) whether to call poe api. By default it should write to local /bots.

Use /responses API for models which support it based on ```supported_endpoints```.

# Sample model file:
```json
{
  "capabilities": {
    "family": "gpt-5",
    "limits": {
      "max_context_window_tokens": 400000,
      "max_output_tokens": 128000,
      "max_prompt_tokens": 128000,
      "vision": {
        "max_prompt_image_size": 3145728,
        "max_prompt_images": 1,
        "supported_media_types": [
          "image/jpeg",
          "image/png",
          "image/webp",
          "image/gif"
        ]
      }
    },
    "object": "model_capabilities",
    "supports": {
      "parallel_tool_calls": true,
      "streaming": true,
      "structured_outputs": true,
      "tool_calls": true,
      "vision": true
    },
    "tokenizer": "o200k_base",
    "type": "chat"
  },
  "id": "gpt-5",
  "model_picker_category": "versatile",
  "model_picker_enabled": true,
  "name": "GPT-5",
  "object": "model",
  "policy": {
    "state": "enabled",
    "terms": "Enable access to the latest GPT-5 model from OpenAI. [Learn more about how GitHub Copilot serves GPT-5](https://gh.io/copilot-openai)."
  },
  "preview": false,
  "supported_endpoints": [
    "/chat/completions",
    "/responses"
  ],
  "vendor": "Azure OpenAI",
  "version": "gpt-5"
}
```