# The linux-dev image plus a Secret Service for SafeStorageTest's libsecret runs (eng/docker/with-secret-service.sh):
# gnome-keyring, libsecret, and PyGObject for the fake service that never answers.
FROM oriel-linux-dev

RUN apt-get update && apt-get install -y --no-install-recommends \
      gnome-keyring libsecret-1-0 python3-gi \
  && rm -rf /var/lib/apt/lists/*
