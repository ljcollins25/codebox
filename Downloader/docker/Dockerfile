FROM kasmweb/brave:1.16.0

USER root

RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg ca-certificates curl && \
    curl -fsSL https://deb.nodesource.com/setup_20.x | bash - && \
    apt-get install -y --no-install-recommends nodejs && \
    rm -rf /var/lib/apt/lists/*

COPY mount-profile.sh /dockerstartup/mount-profile.sh
COPY entrypoint.sh /dockerstartup/entrypoint.sh
COPY download.sh /dockerstartup/download.sh
COPY download-entrypoint.sh /dockerstartup/download-entrypoint.sh
RUN chmod +x /dockerstartup/mount-profile.sh /dockerstartup/entrypoint.sh /dockerstartup/download.sh /dockerstartup/download-entrypoint.sh

ENTRYPOINT ["/dockerstartup/entrypoint.sh"]
CMD ["--wait"]
