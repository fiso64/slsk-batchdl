FROM ghcr.io/linuxserver/baseimage-alpine:3.20 as base

FROM base as build

RUN \
  echo "**** install build packages ****" && \
  apk --no-cache add \
    binutils-gold  \
    openssl \
    zlib \
    libstdc++ \
    dotnet6-sdk

WORKDIR /app

COPY --chown=root:root . /app

RUN dotnet publish -c Release -r linux-musl-x64 -p:PublishSingleFile=true -p:PublishTrimmed=true --self-contained true \
    && rm -f slsk-batchdl/bin/Release/net6.0/osx-x64/publish/*.pdb

FROM base as app

ENV TZ=Etc/GMT

RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone

RUN \
  echo "**** install runtime packages ****" && \
  apk --no-cache add \
    dotnet6-runtime && \
  echo "**** cleanup ****" && \
  rm -rf \
    /root/.cache \
    /tmp/*

ENV DOCKER_MODS=linuxserver/mods:universal-cron

COPY docker/root/ /

COPY --from=build /app/slsk-batchdl/bin/Release/net6.0/linux-musl-x64/publish/* /usr/bin/