# poe: name=Azure-Sas-Bot

class SecretStore:
    # Replace this with your actual secret value
    SECRET_VALUE = "YOUR_SECRET_HERE"

    def run(self):
        # Output the secret - caller captures via .text without displaying
        with poe.start_message() as msg:
            msg.write(self.SECRET_VALUE)

if __name__ == "__main__":
    SecretStore().run()