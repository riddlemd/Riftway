# The linux-dev image plus the X11 window managers of the big desktops, for test-linux.sh's RIFTWAY_WM.
# Built on top of the local riftway-linux-dev image so the plain image stays openbox-only and small:
#   docker build -t riftway-linux-dev-wm -f eng/docker/linux-dev-wm.Dockerfile eng/docker
ARG BASE=riftway-linux-dev:latest
FROM ${BASE}

RUN apt-get update && apt-get install -y --no-install-recommends \
      mutter xfwm4 kwin-x11 \
  && rm -rf /var/lib/apt/lists/*
