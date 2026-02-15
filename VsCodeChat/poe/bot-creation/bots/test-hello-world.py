# poe: name=Hello-World

from fastapi_poe.types import SettingsResponse

poe.update_settings(SettingsResponse(
    introduction_message="Hi! I'm Hello World bot. Send me any message and I'll greet you!",
))

class HelloWorld:
    def run(self):
        with poe.start_message() as msg:
            msg.write("Hello world!")

if __name__ == "__main__":
    bot = HelloWorld()
    bot.run()
