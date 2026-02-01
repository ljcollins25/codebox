# poe: name=Opus-Web-Tools
# poe: privacy_shield=half

import requests

# Configure which bot to use (must support tool calling)
MAIN_BOT = "vsc-op-1-c"
TEXT_PROCESSOR_BOT = "gpt-5-nano"


def web_search(
    query: poe.Doc[str, "The search query to look up on the web"]
) -> str:
    """Search the web for current information using a search engine."""
    response = poe.call("Brave-Search", query)
    return response.text


def web_request(
    url: poe.Doc[str, "The URL to fetch"],
    summarize: poe.Doc[bool, "Whether to summarize the fetched content"] = False,
    extract_text: poe.Doc[bool, "Whether to extract only the main text content, removing HTML markup"] = False
) -> str:
    """Make an HTTP request to fetch content from a URL. Optionally summarize or extract the main text."""
    try:
        resp = requests.get(url, timeout=30, headers={"User-Agent": "Mozilla/5.0"})
        resp.raise_for_status()
        content = resp.text
    except requests.RequestException as e:
        return f"Error fetching URL: {e}"

    # Truncate to avoid token limits
    max_chars = 50000

    if extract_text:
        response = poe.call(
            TEXT_PROCESSOR_BOT,
            f"Extract only the main text content from this HTML, removing all markup, navigation, and boilerplate elements. Return just the core content:\n\n{content[:max_chars]}"
        )
        content = response.text

    if summarize:
        response = poe.call(
            TEXT_PROCESSOR_BOT,
            f"Summarize the following content concisely:\n\n{content[:max_chars]}"
        )
        content = response.text

    # Return truncated content if still too long
    return content[:max_chars] if len(content) > max_chars else content


class OpusWebTools:
    def run(self):
        poe.call(
            MAIN_BOT,
            poe.default_chat,
            tools=[web_search, web_request],
            output=poe.default_chat,
            adopt_current_bot_name=True
        )


if __name__ == "__main__":
    bot = OpusWebTools()
    bot.run()
