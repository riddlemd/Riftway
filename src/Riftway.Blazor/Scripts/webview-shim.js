// Prepended to blazor.webview.js: CEF has neither WebView2's chrome.webview nor WebKit's message handlers,
// so blazor.webview.js falls back to window.external, which Chromium lets a page replace.
(() => {
  const channel = window.__riftwayBlazor;
  if (channel) window.external = { sendMessage: (m) => channel.send(m), receiveMessage: (cb) => channel.receive(cb) };
})();
