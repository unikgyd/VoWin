FROM ubuntu:26.04@sha256:f144425ff09be612d6d9ad965196e9cdc23dae1f42110a8a11a3e9a8198759f7

ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
    && apt-get install -y --no-install-recommends asterisk asterisk-core-sounds-en-gsm ca-certificates \
    && groupmod -g 10001 asterisk \
    && usermod -u 10001 -g 10001 asterisk \
    && mkdir -p /run/asterisk \
    && chown -R 10001:10001 /etc/asterisk /var/lib/asterisk /var/log/asterisk /var/spool/asterisk /run/asterisk \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

USER 10001:10001
ENTRYPOINT ["/usr/sbin/asterisk"]
CMD ["-f", "-g", "-vv"]
