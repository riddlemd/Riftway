# Linux build-and-run environment for Oriel: .NET SDK, the NativeAOT toolchain, CEF's runtime
# libraries, and a headless X server with a window manager so Views windows get real decorations.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble

RUN apt-get update && apt-get install -y --no-install-recommends \
      clang zlib1g-dev python3 bzip2 rsync \
      libnss3 libnspr4 libatk1.0-0t64 libatk-bridge2.0-0t64 libcups2t64 libdrm2 libxkbcommon0 \
      libxcomposite1 libxdamage1 libxfixes3 libxrandr2 libgbm1 libpango-1.0-0 libcairo2 \
      libasound2t64 libgtk-3-0t64 libx11-xcb1 libxshmfence1 libdbus-1-3 libglib2.0-0t64 \
      fonts-liberation fonts-noto-cjk \
      xvfb openbox wmctrl xdotool imagemagick \
  && rm -rf /var/lib/apt/lists/*

ENV ORIEL_CEF_CACHE=/cef-cache DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /work

# Chromium refuses to sandbox as root, so sandboxed runs use this user.
RUN useradd --create-home oriel
