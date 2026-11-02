# Linux build-and-run environment for Riftway: .NET SDK, the NativeAOT toolchain, CEF's runtime
# libraries, and a headless X server with a window manager so Views windows get real decorations.
# xcompmgr is the compositing manager WindowTest starts for transparent windows; x11-utils has xwininfo;
# x11-xserver-utils has xrandr, which WindowTest's display-change checks drive. squashfs-tools and zstd are what
# Velopack's vpk needs for an AppImage and its delta packages.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble

RUN apt-get update && apt-get install -y --no-install-recommends \
      clang zlib1g-dev python3 bzip2 rsync \
      libnss3 libnspr4 libatk1.0-0t64 libatk-bridge2.0-0t64 libcups2t64 libdrm2 libxkbcommon0 \
      libxcomposite1 libxdamage1 libxfixes3 libxrandr2 libgbm1 libpango-1.0-0 libcairo2 \
      libasound2t64 libgtk-3-0t64 libx11-xcb1 libxshmfence1 libdbus-1-3 libglib2.0-0t64 libglib2.0-bin \
      fonts-liberation fonts-noto-cjk \
      xvfb openbox wmctrl xdotool imagemagick libnss3-tools \
      xcompmgr x11-utils x11-xserver-utils squashfs-tools zstd \
  && rm -rf /var/lib/apt/lists/*

ENV RIFTWAY_CEF_CACHE=/cef-cache DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /work

# Chromium refuses to sandbox as root, so sandboxed runs use this user.
RUN useradd --create-home riftway
