# The linux-dev image plus the X11 window managers of the big desktops, for test-linux.sh's ORIEL_WM.
# Built on top of the local oriel-linux-dev image so the plain image stays openbox-only and small:
#   docker build -t oriel-linux-dev-wm -f eng/docker/linux-dev-wm.Dockerfile eng/docker
ARG BASE=oriel-linux-dev:latest
FROM ${BASE}

RUN apt-get update && apt-get install -y --no-install-recommends \
      mutter xfwm4 kwin-x11 \
  && rm -rf /var/lib/apt/lists/*
