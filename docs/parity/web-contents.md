# webContents parity

Against Electron **v44.5.1** (`docs/api/web-contents.md`, `navigation-history.md`, `web-frame-main.md`). Audited 2026-10-04; update this file as items land.

Status: **done**, **partial** (exists with a documented gap; DRIFT.md id in the note), **missing**, or **n/a** (Node/`<webview>` features and deprecated APIs). Window-side items are in [browser-window.md](browser-window.md).

## Summary

Percentages are over the non-n/a items; the second counts a partial item as half.

| Area | Items | Done | Partial | Missing | n/a | % done | % done + partial/2 |
|---|---|---|---|---|---|---|---|
| Static methods | 5 | 0 | 0 | 5 | 0 | 0.0% | 0.0% |
| Instance events | 59 | 8 | 1 | 48 | 2 | 14.0% | 14.9% |
| Instance methods | 118 | 23 | 7 | 80 | 8 | 20.9% | 24.1% |
| Instance properties | 18 | 4 | 2 | 11 | 1 | 23.5% | 29.4% |
| navigationHistory | 14 | 10 | 2 | 2 | 0 | 71.4% | 78.6% |
| WebFrameMain events and statics | 3 | 1 | 0 | 2 | 0 | 33.3% | 33.3% |
| WebFrameMain methods | 9 | 1 | 3 | 5 | 0 | 11.1% | 27.8% |
| WebFrameMain properties | 15 | 9 | 0 | 6 | 0 | 60.0% | 60.0% |
| **Total** | 241 | 56 | 15 | 159 | 11 | 24.3% | 27.6% |

## Checklist

### Static methods (`webContents`)

| Electron | Oriel | Status | Note |
|---|---|---|---|
| `getAllWebContents()` | — | missing |  |
| `getFocusedWebContents()` | — | missing |  |
| `fromId(id)` | — | missing | WebContents has no `id`. |
| `fromFrame(frame)` | — | missing |  |
| `fromDevToolsTargetId(targetId)` | — | missing |  |

### Instance events

| Electron | Oriel | Status | Note |
|---|---|---|---|
| `did-finish-load` | `DidFinishLoad` | done |  |
| `did-fail-load` | `DidFailLoad` | done | `LoadFailedEventArgs`. |
| `did-start-loading` | `DidStartLoading` | done |  |
| `did-stop-loading` | `DidStopLoading` | done |  |
| `dom-ready` | `DomReady` | done |  |
| `page-title-updated` | `PageTitleUpdated` | done | Cancel keeps the window title. |
| `did-create-window` | `DidCreateWindow` | done |  |
| `context-menu` | `ContextMenu` | partial | Params `frame`, `linkText`, `altText`, `selectionRect`, `menuSourceType`, `formControlType`, `referrerPolicy`, `suggestedFilename`, `selectionStartOffset` missing (C8). |
| `will-attach-webview` | — | n/a | <webview> |
| `did-attach-webview` | — | n/a | <webview> |
| `did-fail-provisional-load` | — | missing |  |
| `did-frame-finish-load` | — | missing |  |
| `page-favicon-updated` | — | missing |  |
| `content-bounds-updated` | — | missing |  |
| `will-navigate` | — | missing |  |
| `will-frame-navigate` | — | missing |  |
| `did-start-navigation` | — | missing |  |
| `will-redirect` | — | missing |  |
| `did-redirect-navigation` | — | missing |  |
| `did-navigate` | — | missing |  |
| `did-frame-navigate` | — | missing |  |
| `did-navigate-in-page` | — | missing |  |
| `will-prevent-unload` | — | missing |  |
| `render-process-gone` | — | missing |  |
| `unresponsive` | — | missing |  |
| `responsive` | — | missing |  |
| `destroyed` | `Destroyed` | done |  |
| `input-event` | — | missing |  |
| `before-input-event` | — | missing |  |
| `before-mouse-event` | — | missing |  |
| `enter-html-full-screen` | — | missing |  |
| `leave-html-full-screen` | — | missing |  |
| `zoom-changed` | — | missing |  |
| `blur` | — | missing |  |
| `focus` | — | missing |  |
| `devtools-open-url` | — | missing |  |
| `devtools-search-query` | — | missing |  |
| `devtools-opened` | — | missing |  |
| `devtools-closed` | — | missing |  |
| `devtools-focused` | — | missing |  |
| `certificate-error` | — | missing |  |
| `select-client-certificate` | — | missing |  |
| `login` | — | missing |  |
| `found-in-page` | — | missing |  |
| `media-started-playing` | — | missing |  |
| `media-paused` | — | missing |  |
| `audio-state-changed` | — | missing |  |
| `did-change-theme-color` | — | missing |  |
| `update-target-url` | — | missing |  |
| `cursor-changed` | — | missing |  |
| `select-bluetooth-device` | — | missing |  |
| `paint` | — | missing |  |
| `devtools-reload-page` | — | missing |  |
| `console-message` | — | missing |  |
| `preload-error` | — | missing |  |
| `ipc-message` | — | missing | Only `IpcMain` listeners exist; no per-contents event. |
| `ipc-message-sync` | — | missing | Sync IPC (I2). |
| `preferred-size-changed` | — | missing |  |
| `frame-created` | — | missing |  |

### Instance methods

| Electron | Oriel | Status | Note |
|---|---|---|---|
| `loadURL(url, options)` | `LoadUrl` | partial | Returns Task; `baseURLForDataURL` throws, `UploadFile` offset/length throw, no client hints, reload drops options (C1, C2, C3, C5). |
| `loadFile(filePath, options)` | `LoadFile` | partial | `query` and `hash`, no `search`. |
| `getURL()` | `GetUrl` | done |  |
| `getTitle()` | `GetTitle` | done |  |
| `reload()` | `Reload` | done |  |
| `reloadIgnoringCache()` | `ReloadIgnoringCache` | done |  |
| `setUserAgent(userAgent)` | `SetUserAgent` | done |  |
| `getUserAgent()` | `GetUserAgent` | partial | Empty until DevTools answers, just after window creation (C4). |
| `executeJavaScript(code, userGesture)` | `ExecuteJavaScriptAsync` | partial | JSON result only, `JsonNode?` (C6, S3). |
| `setWindowOpenHandler(handler)` | `SetWindowOpenHandler` | partial | `features` rebuilt, no `referrer`/`postBody`, no `createWindow` (O1, O2, O3). |
| `setZoomFactor(factor)` | `SetZoomFactor` | done |  |
| `getZoomFactor()` | `GetZoomFactor` | done |  |
| `setZoomLevel(level)` | `SetZoomLevel` | done |  |
| `getZoomLevel()` | `GetZoomLevel` | done |  |
| `undo()` | `Undo` | done |  |
| `redo()` | `Redo` | done |  |
| `cut()` | `Cut` | done |  |
| `copy()` | `Copy` | done |  |
| `paste()` | `Paste` | done |  |
| `pasteAndMatchStyle()` | `PasteAndMatchStyle` | done |  |
| `delete()` | `Delete` | done |  |
| `selectAll()` | `SelectAll` | done |  |
| `openDevTools(options)` | `OpenDevTools` | partial | No options; own window only, no docked modes (W7). |
| `closeDevTools()` | `CloseDevTools` | done |  |
| `isDevToolsOpened()` | `IsDevToolsOpened` | done |  |
| `toggleDevTools()` | `ToggleDevTools` | done |  |
| `send(channel, ...args)` | `Send` | done | Args are JSON (I1). |
| `sendInputEvent(inputEvent)` | `SendInputEvent` | partial | Keyboard and mouse down/up/move only; no wheel, mouseEnter/Leave, contextMenu (C7). |
| `downloadURL()` | — | missing |  |
| `isDestroyed()` | `IsDestroyed` | done |  |
| `close()` | `Close(waitForBeforeUnload)` | done | A window's page closes its window |
| `focus()` | — | missing |  |
| `isFocused()` | — | missing |  |
| `isLoading()` | — | missing |  |
| `isLoadingMainFrame()` | — | missing |  |
| `isWaitingForResponse()` | — | missing |  |
| `stop()` | — | missing |  |
| `isCrashed()` | — | missing |  |
| `forcefullyCrashRenderer()` | — | missing |  |
| `insertCSS()` | — | missing |  |
| `removeInsertedCSS()` | — | missing |  |
| `executeJavaScriptInIsolatedWorld()` | — | missing |  |
| `setIgnoreMenuShortcuts()` | — | missing |  |
| `setAudioMuted()` | — | missing |  |
| `isAudioMuted()` | — | missing |  |
| `isCurrentlyAudible()` | — | missing |  |
| `setCaretBrowsingEnabled()` | — | missing |  |
| `isCaretBrowsingEnabled()` | — | missing |  |
| `setZoomMode()` | — | missing |  |
| `getZoomMode()` | — | missing |  |
| `setVisualZoomLevelLimits()` | — | missing |  |
| `centerSelection()` | — | missing |  |
| `copyImageAt()` | — | missing |  |
| `copyVideoFrameAt()` | — | missing |  |
| `saveVideoFrameAs()` | — | missing |  |
| `unselect()` | — | missing |  |
| `scrollToTop()` | — | missing |  |
| `scrollToBottom()` | — | missing |  |
| `adjustSelection()` | — | missing |  |
| `replace()` | — | missing |  |
| `replaceMisspelling()` | — | missing |  |
| `insertText()` | — | missing |  |
| `findInPage()` | — | missing |  |
| `stopFindInPage()` | — | missing |  |
| `capturePage()` | — | missing |  |
| `isBeingCaptured()` | — | missing |  |
| `getPrintersAsync()` | — | missing |  |
| `print()` | — | missing |  |
| `printToPDF()` | — | missing |  |
| `addWorkSpace()` | — | missing |  |
| `removeWorkSpace()` | — | missing |  |
| `setDevToolsWebContents()` | — | missing |  |
| `isDevToolsFocused()` | — | missing |  |
| `getDevToolsTitle()` | — | missing |  |
| `setDevToolsTitle()` | — | missing |  |
| `inspectElement()` | — | missing |  |
| `inspectSharedWorker()` | — | missing |  |
| `inspectSharedWorkerById()` | — | missing |  |
| `getAllSharedWorkers()` | — | missing |  |
| `inspectServiceWorker()` | — | missing |  |
| `sendToFrame()` | — | missing |  |
| `postMessage()` | — | missing |  |
| `enableDeviceEmulation()` | — | missing |  |
| `disableDeviceEmulation()` | — | missing |  |
| `beginFrameSubscription()` | — | missing |  |
| `endFrameSubscription()` | — | missing |  |
| `startDrag()` | — | missing |  |
| `savePage()` | — | missing |  |
| `showDefinitionForSelection()` | — | missing |  |
| `isOffscreen()` | — | missing |  |
| `startPainting()` | — | missing |  |
| `stopPainting()` | — | missing |  |
| `isPainting()` | — | missing |  |
| `setFrameRate()` | — | missing |  |
| `getFrameRate()` | — | missing |  |
| `invalidate()` | — | missing |  |
| `getWebRTCIPHandlingPolicy()` | — | missing |  |
| `setWebRTCIPHandlingPolicy()` | — | missing |  |
| `getWebRTCUDPPortRange()` | — | missing |  |
| `setWebRTCUDPPortRange()` | — | missing |  |
| `getMediaSourceId()` | — | missing |  |
| `getOrCreateDevToolsTargetId()` | — | missing |  |
| `getOSProcessId()` | — | missing |  |
| `getProcessId()` | — | missing |  |
| `clone()` | — | missing |  |
| `takeHeapSnapshot()` | — | missing |  |
| `getBackgroundThrottling()` | — | missing |  |
| `setBackgroundThrottling()` | — | missing |  |
| `getType()` | — | missing |  |
| `setImageAnimationPolicy()` | — | missing |  |
| `canGoBack()` | — | n/a | Deprecated; use navigationHistory. |
| `canGoForward()` | — | n/a | Deprecated; use navigationHistory. |
| `canGoToOffset()` | — | n/a | Deprecated; use navigationHistory. |
| `clearHistory()` | — | n/a | Deprecated; use navigationHistory. |
| `goBack()` | — | n/a | Deprecated; use navigationHistory. |
| `goForward()` | — | n/a | Deprecated; use navigationHistory. |
| `goToIndex()` | — | n/a | Deprecated; use navigationHistory. |
| `goToOffset()` | — | n/a | Deprecated; use navigationHistory. |

### Instance properties

| Electron | Oriel | Status | Note |
|---|---|---|---|
| `audioMuted` | — | missing |  |
| `userAgent` | `SetUserAgent / GetUserAgent` | partial | Getter empty until DevTools answers (C4). |
| `zoomLevel` | `SetZoomLevel / GetZoomLevel` | done |  |
| `zoomFactor` | `SetZoomFactor / GetZoomFactor` | done |  |
| `navigationHistory` | `NavigationHistory` | done | Members in their own table below. |
| `mainFrame` | `MainFrame` | partial | Null until the frame tree has been read, just after window creation (F4). |
| `hostWebContents` | — | n/a | <webview> only. |
| `focusedFrame` | — | missing | F3. |
| `ipc` | — | missing |  |
| `caretBrowsingEnabled` | — | missing |  |
| `frameRate` | — | missing |  |
| `id` | — | missing |  |
| `session` | `Session` | done | See DRIFT E1-E11 for the session gaps. |
| `devToolsWebContents` | — | missing |  |
| `debugger` | — | missing |  |
| `backgroundThrottling` | — | missing |  |
| `opener` | — | missing |  |
| `zoomMode` | — | missing |  |

### `navigationHistory` (`NavigationHistory`)

| Electron | Oriel | Status | Note |
|---|---|---|---|
| `canGoBack()` | `CanGoBack` | done |  |
| `canGoForward()` | `CanGoForward` | done |  |
| `canGoToOffset(offset)` | `CanGoToOffset` | done |  |
| `clear()` | `Clear` | done | Resets via DevTools; see N3. |
| `getActiveIndex()` | `GetActiveIndex` | done |  |
| `getEntryAtIndex(index)` | `GetEntryAtIndex` | done | Entry has `url` and `title` (no `pageState`, N1). |
| `goBack()` | `GoBack` | done |  |
| `goForward()` | `GoForward` | done |  |
| `goToIndex(index)` | `GoToIndex` | partial | Via the page's `history.go`; does nothing with `Javascript = false` (N2). |
| `goToOffset(offset)` | `GoToOffset` | partial | Via the page's `history.go`; does nothing with `Javascript = false` (N2). |
| `length()` | `Length` | done |  |
| `removeEntryAtIndex(index)` | — | missing | N1. |
| `getAllEntries()` | `GetAllEntries` | done |  |
| `restore(options)` | — | missing | N1. |

### `webFrameMain` statics and events (`WebFrameMain`)

| Electron | Oriel | Status | Note |
|---|---|---|---|
| `fromId(processId, routingId)` | — | missing | F2. |
| `fromFrameToken(processId, frameToken)` | — | missing | F2. |
| `dom-ready (event)` | `DomReady` | done |  |

### `WebFrameMain` instance methods

| Electron | Oriel | Status | Note |
|---|---|---|---|
| `executeJavaScript(code, userGesture)` | `ExecuteJavaScriptAsync` | partial | JSON result only; cross-process frames cannot run script (F1, C6). |
| `reload()` | `Reload` | partial | Cross-process frames cannot reload (F1). |
| `isDestroyed()` | `IsDestroyed` | done |  |
| `send(channel, ...args)` | `Send` | partial | Dropped for cross-process frames and before the preload runs (F1). |
| `postMessage(channel, message, transfer)` | — | missing | F2. |
| `collectJavaScriptCallStack()` | — | missing | F2. |
| `copyVideoFrameAt(x, y)` | — | missing |  |
| `saveVideoFrameAs(x, y)` | — | missing |  |
| `printToPDF(options)` | — | missing |  |

### `WebFrameMain` instance properties

| Electron | Oriel | Status | Note |
|---|---|---|---|
| `url` | `Url` | done |  |
| `origin` | `Origin` | done |  |
| `top` | `Top` | done |  |
| `parent` | `Parent` | done |  |
| `frames` | `Frames` | done |  |
| `framesInSubtree` | `FramesInSubtree` | done |  |
| `name` | `Name` | done |  |
| `frameToken` | `FrameToken` | done | Chromium's DevTools frame token (F2). |
| `detached` | `Detached` | done |  |
| `ipc` | — | missing | F2 for the ids. |
| `frameTreeNodeId` | — | missing | F2 for the ids. |
| `osProcessId` | — | missing | F2 for the ids. |
| `processId` | — | missing | F2 for the ids. |
| `routingId` | — | missing | F2 for the ids. |
| `visibilityState` | — | missing | F2 for the ids. |
