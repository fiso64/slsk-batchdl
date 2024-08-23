FROM ghcr.io/linuxserver/baseimage-alpine:3.20

ENV TZ=Etc/GMT

RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone

RUN \
  echo "**** install packages ****" && \
  apk --no-cache add \
    binutils-gold  \
    openssl \
    zlib \
    libstdc++ \
    dotnet6-sdk \
    aspnetcore6-runtime \
    bash

ENV DOCKER_MODS=linuxserver/mods:universal-cron

COPY docker/root/ /

WORKDIR /app

COPY --chown=abc:abc . /app

RUN rm -rf /app/docker

RUN dotnet publish -c Release -r linux-x64 -p:PublishSingleFile=true -p:PublishTrimmed=true --self-contained true