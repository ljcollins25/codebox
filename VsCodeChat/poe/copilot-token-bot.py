# poe: name=Copilot-Token-Bot
# poe: description=Gets a Copilot token from VSC-CPT server bot

"""
Script bot that calls VSC-CPT server bot to get a GitHub Copilot token.

This bot queries the VSC-CPT server bot (implemented by the copilot-token-bot 
Cloudflare Worker) to obtain a Copilot token through GitHub's device flow.

Usage:
- Send any message to get the current token status
- Send "refresh" to force refresh the Copilot token
- Send "reset" to clear all cached tokens and start fresh
"""

import json
from fastapi_poe.types import (
    SettingsResponse,
    ParameterControls,
    Section,
    TextField,
    ToggleSwitch,
    Slider,
    DropDown,
    ValueNamePair,
)

# Configure bot settings
poe.update_settings(SettingsResponse(
    introduction_message="I get Copilot tokens from the VSC-CPT server bot. Send any message to get a token, 'refresh' to force refresh, or 'reset' to start fresh.",
    parameter_controls=ParameterControls(
        sections=[
            Section(
                name="Token Settings",
                controls=[
                    TextField(
                        label="Salt",
                        parameter_name="salt",
                        description="Optional salt to create separate token namespaces",
                        default_value="",
                        placeholder="my-namespace",
                    ),
                    DropDown(
                        label="Mode",
                        parameter_name="mode",
                        description="Response output mode",
                        default_value="query_token",
                        options=[
                            ValueNamePair(value="", name="Normal (verbose)"),
                            ValueNamePair(value="query_token", name="Query Token (JSON only)"),
                        ],
                    ),
                    ToggleSwitch(
                        label="Markdown",
                        parameter_name="markdown",
                        description="Wrap JSON output in markdown code blocks",
                        default_value=True,
                    ),
                ],
            ),
            Section(
                name="Polling",
                collapsed_by_default=True,
                controls=[
                    Slider(
                        label="Poll Interval (seconds)",
                        parameter_name="poll_interval_secs",
                        description="Interval between device flow polls",
                        default_value=5,
                        min_value=5,
                        max_value=30,
                        step=1,
                    ),
                    Slider(
                        label="Poll Count",
                        parameter_name="poll_count",
                        description="Number of times to poll for authorization",
                        default_value=1,
                        min_value=1,
                        max_value=60,
                        step=1,
                    ),
                ],
            ),
        ]
    ),
))


class CopilotTokenBot:
    """Bot that gets Copilot tokens from VSC-CPT server bot."""
    
    def run(self):
        # Get parameters from the query
        params = poe.query.parameters or {}
        
        # Build parameters to pass to VSC-CPT
        vsc_cpt_params = {}
        
        # Pass through configuration parameters
        if params.get("salt"):
            vsc_cpt_params["salt"] = params["salt"]
        if params.get("mode"):
            vsc_cpt_params["mode"] = params["mode"]
        if params.get("markdown"):
            vsc_cpt_params["markdown"] = params["markdown"]
        if params.get("poll_interval_secs"):
            vsc_cpt_params["poll_interval_secs"] = params["poll_interval_secs"]
        if params.get("poll_count"):
            vsc_cpt_params["poll_count"] = params["poll_count"]
        
        # Check for refresh/reset commands in query text
        query_text = poe.query.text.strip().lower()
        if query_text == "refresh":
            vsc_cpt_params["refresh"] = True
        elif query_text == "reset":
            vsc_cpt_params["reset"] = True
        
        # Call VSC-CPT server bot
        yield poe.info("Calling VSC-CPT to get Copilot token...")
        
        response_text = ""
        response_data = None
        
        for chunk in poe.stream_bot(
            bot_name="VSC-CPT",
            query=poe.query.text,
            parameters=vsc_cpt_params,
        ):
            if chunk.data:
                # Parse the data event to get the token info
                try:
                    response_data = json.loads(chunk.data)
                except (json.JSONDecodeError, TypeError):
                    pass
            if chunk.text:
                response_text += chunk.text
        
        # Output the result
        if response_data:
            status = response_data.get("status", "unknown")
            
            if status == "acquired":
                # Successfully got a token
                token = response_data.get("copilot_token", "")
                expires_at = response_data.get("copilot_expires_at", "")
                acquired_at = response_data.get("copilot_acquired_at", "")
                
                yield poe.text("✅ **Copilot Token Acquired**\n\n")
                yield poe.text(f"**Expires:** {expires_at}\n")
                yield poe.text(f"**Acquired:** {acquired_at}\n\n")
                yield poe.text("**Token:**\n```\n")
                yield poe.text(token)
                yield poe.text("\n```\n")
                
                # Also emit the data for programmatic access
                yield poe.data(response_data)
                
            elif status == "authorization_pending":
                # Need to complete device flow
                user_code = response_data.get("user_code", "")
                verification_uri = response_data.get("verification_uri", "")
                
                yield poe.text("🔐 **GitHub Authorization Required**\n\n")
                yield poe.text(f"Visit: {verification_uri}\n")
                yield poe.text(f"Enter code: **{user_code}**\n\n")
                yield poe.text("Send another message after authorizing.")
                
                yield poe.data(response_data)
                
            else:
                # Some other status (error, etc.)
                yield poe.text(f"⚠️ **Status:** {status}\n\n")
                if response_text:
                    yield poe.text(response_text)
                yield poe.data(response_data)
        else:
            # No structured data, just output the text response
            if response_text:
                yield poe.text(response_text)
            else:
                yield poe.text("❌ No response from VSC-CPT server bot.")


# Run the bot
CopilotTokenBot().run()
