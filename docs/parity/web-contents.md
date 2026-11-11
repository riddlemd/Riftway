# webContents parity

Against Electron **v44.5.1** (`docs/api/web-contents.md`, `navigation-history.md`, `web-frame-main.md`). Audited 2026-10-04, recounted 2026-10-07; update this file as items land.

Status: **done**, **partial** (exists with a documented gap; DRIFT.md id in the note), **missing**, or **n/a** (Node/`<webview>` features and deprecated APIs). Window-side items are in [browser-window.md](browser-window.md).

## Summary

Percentages are over the non-n/a items; the second counts a partial item as half.

| Area | Items | Done | Partial | Missing | n/a | % done | % done + partial/2 |
|---|---|---|---|---|---|---|---|
| Static methods | 5 | 3 | 2 | 0 | 0 | 60.0% | 80.0% |
| Instance events | 59 | 31 | 18 | 8 | 2 | 54.4% | 70.2% |
| Instance methods | 118 | 44 | 38 | 28 | 8 | 40.0% | 57.3% |
| Instance properties | 18 | 10 | 3 | 4 | 1 | 58.8% | 67.6% |
| navigationHistory | 14 | 12 | 0 | 2 | 0 | 85.7% | 85.7% |
| WebFrameMain events and statics | 3 | 1 | 1 | 1 | 0 | 33.3% | 50.0% |
| WebFrameMain methods | 9 | 1 | 5 | 3 | 0 | 11.1% | 38.9% |
| WebFrameMain properties | 15 | 10 | 3 | 2 | 0 | 66.7% | 76.7% |
| **Total** | 241 | 112 | 70 | 48 | 11 | 48.7% | 63.9% |

## Checklist

### Static methods (`webContents`)

| Electron | Riftway | Status | Note |
|---|---|---|---|
| `getAllWebContents()` | `GetAllWebContents` | partial | Window pages only (C17). |
| `getFocusedWebContents()` | `GetFocusedWebContents` | partial | Never a DevTools page (C17). |
| `fromId(id)` | `FromId` | done |  |
| `fromFrame(frame)` | `FromFrame` | done |  |
| `fromDevToolsTargetId(targetId)` | `FromDevToolsTargetId` | done |  |

### Instance events

| Electron | Riftway | Status | Note |
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
| `did-fail-provisional-load` | `DidFailProvisionalLoad` | done | `DidFailLoad` now leaves out cancelled loads, as Electron's. |
| `did-frame-finish-load` | `DidFrameFinishLoad` | partial | No process or routing ids (F2). |
| `page-favicon-updated` | `PageFaviconUpdated` | done |  |
| `content-bounds-updated` | `ContentBoundsUpdated` | done |  |
| `will-navigate` | `WillNavigate` | done | `Initiator` is null (C12). |
| `will-frame-navigate` | `WillFrameNavigate` | done | C12. |
| `did-start-navigation` | `DidStartNavigation` | done | C12. |
| `will-redirect` | `WillRedirect` | done |  |
| `did-redirect-navigation` | `DidRedirectNavigation` | done |  |
| `did-navigate` | `DidNavigate` | done |  |
| `did-frame-navigate` | `DidFrameNavigate` | partial | `Frame` in place of process and routing ids (C12, F2). |
| `did-navigate-in-page` | `DidNavigateInPage` | partial | `Frame` in place of process and routing ids (C12, F2). |
| `will-prevent-unload` | `WillPreventUnload` | done | A refused leave fails the pending `LoadUrl` with `did-fail-load` (C31: every load is remembered). |
| `render-process-gone` | `RenderProcessGone` | partial | No `clean-exit` (C13). |
| `unresponsive` | `Unresponsive` | partial | Real hang not reproduced in the harness (C14). |
| `responsive` | `Responsive` | partial | Real hang not reproduced in the harness (C14). |
| `destroyed` | `Destroyed` | done |  |
| `input-event` | `InputEvent` | partial | Keys from CEF's key handler; mouse and wheel events from the main frame's DOM, a little later; no gesture, touch or pointer events (C41). |
| `before-input-event` | `BeforeInputEvent` | partial | `modifiers` shape (C15); `code`, `isComposing`, `location` derived (C16). |
| `before-mouse-event` | — | missing |  |
| `enter-html-full-screen` | `EnterHtmlFullScreen` | done |  |
| `leave-html-full-screen` | `LeaveHtmlFullScreen` | done |  |
| `zoom-changed` | `ZoomChanged` | partial | From the page's wheel events (C35). |
| `blur` | `Blurred` | partial | Raised when the window loses focus; another view in the window taking focus is not seen (C30). |
| `focus` | `Focused` | done |  |
| `devtools-open-url` | — | missing |  |
| `devtools-search-query` | — | missing |  |
| `devtools-opened` | `DevToolsOpened` | partial | Watched, within a quarter second (C36). |
| `devtools-closed` | `DevToolsClosed` | partial | Watched, within a quarter second (C36). |
| `devtools-focused` | — | missing | Chrome style's DevTools window is not CEF's (C36). |
| `certificate-error` | `CertificateError` | done | The app's `CertificateError` follows, with the same arguments. |
| `select-client-certificate` | `SelectClientCertificate` | done | The app's follows with the same arguments; checked on Linux and Windows (E25). |
| `login` | `Login` | done | The app's `Login` follows, with the same arguments. |
| `found-in-page` | `FoundInPage` | done |  |
| `media-started-playing` | `MediaStartedPlaying` | partial | From the page's media events (C33). |
| `media-paused` | `MediaPaused` | partial | From the page's media events (C33). |
| `audio-state-changed` | — | missing | CEF never reports audio for Chrome-style browsers (C23). |
| `did-change-theme-color` | `DidChangeThemeColor` | done | Computed in the main frame by Blink's rule. |
| `update-target-url` | `UpdateTargetUrl` | done |  |
| `cursor-changed` | `CursorChanged` | done |  |
| `select-bluetooth-device` | — | missing |  |
| `paint` | — | missing |  |
| `devtools-reload-page` | — | missing |  |
| `console-message` | `ConsoleMessage` | partial | No `frame` (C28). |
| `preload-error` | `PreloadError` | done | The error arrives as its name, message and stack. |
| `ipc-message` | `IpcMessage` | done |  |
| `ipc-message-sync` | `IpcMessageSync` | done | Through a synchronous XHR (I9). |
| `preferred-size-changed` | `PreferredSizeChanged` | partial | Measured in the page after layout-changing events, not after every layout (C42). |
| `frame-created` | `FrameCreated` | partial | Subframes only (C29). |

### Instance methods

| Electron | Riftway | Status | Note |
|---|---|---|---|
| `loadURL(url, options)` | `LoadUrl` | partial | Returns Task; `baseURLForDataURL` throws, `UploadFile` offset/length throw, no client hints, reload drops options (C1, C2, C3, C5). |
| `loadFile(filePath, options)` | `LoadFile` | done | `query`, `search`, `hash` |
| `getURL()` | `GetUrl` | done |  |
| `getTitle()` | `GetTitle` | done |  |
| `reload()` | `Reload` | done |  |
| `reloadIgnoringCache()` | `ReloadIgnoringCache` | done |  |
| `setUserAgent(userAgent)` | `SetUserAgent` | done |  |
| `getUserAgent()` | `GetUserAgent` | done | The app's fallback until overridden |
| `executeJavaScript(code, userGesture)` | `ExecuteJavaScriptAsync` | partial | JSON result only, `JsonNode?` (C6, S3). |
| `setWindowOpenHandler(handler)` | `SetWindowOpenHandler` | partial | `features` rebuilt, no `referrer`/`postBody`; `createWindow` must adopt the page before it returns (O1, O2, O3). |
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
| `sendInputEvent(inputEvent)` | `SendInputEvent` | done | Keyboard; mouse down/up/move/enter/leave, contextMenu and wheel |
| `downloadURL(url, options)` | `DownloadUrl` | partial | No `headers` option (E16); the session raises `WillDownload`. |
| `isDestroyed()` | `IsDestroyed` | done |  |
| `close()` | `Close` | done |  |
| `focus()` | `Focus` | done |  |
| `isFocused()` | `IsFocused` | done |  |
| `isLoading()` | `IsLoading` | done |  |
| `isLoadingMainFrame()` | `IsLoadingMainFrame` | partial | Derived from DevTools frame events (C19). |
| `isWaitingForResponse()` | `IsWaitingForResponse` | partial | Ends at commit, from DevTools frame events (C19). |
| `stop()` | `Stop` | done |  |
| `isCrashed()` | `IsCrashed` | done | Until a reload's new renderer is alive. |
| `forcefullyCrashRenderer()` | `ForcefullyCrashRenderer` | partial | Crashes (Electron's Linux route) on every OS (C27). |
| `insertCSS()` | `InsertCssAsync` | partial | Inspector sheet; `user` origin applied as author (C25). |
| `removeInsertedCSS()` | `RemoveInsertedCssAsync` | done |  |
| `executeJavaScriptInIsolatedWorld()` | `ExecuteJavaScriptInIsolatedWorldAsync` | partial | DevTools-named worlds, JSON results (C26). |
| `setIgnoreMenuShortcuts()` | `SetIgnoreMenuShortcuts` | done | Windows and Linux; macOS passes on screen (macOS 26, arm64 and x64) (C34). |
| `setAudioMuted()` | `SetAudioMuted` | done |  |
| `isAudioMuted()` | `IsAudioMuted` | done |  |
| `isCurrentlyAudible()` | — | missing | CEF never reports audio for Chrome-style browsers (C23). |
| `setCaretBrowsingEnabled()` | `SetCaretBrowsingEnabled` | partial | Applies to every page of the session (C53). |
| `isCaretBrowsingEnabled()` | `IsCaretBrowsingEnabled` | partial | What this page set; the session's pages share the setting (C53). |
| `setZoomMode()` | — | missing |  |
| `getZoomMode()` | — | missing |  |
| `setVisualZoomLevelLimits()` | `SetVisualZoomLevelLimits` | partial | Pinch on or off; off on Windows/Linux (C37) |
| `centerSelection()` | `CenterSelection` | partial | Script; does nothing off macOS, as Electron (C20). |
| `copyImageAt()` | `CopyImageAt` | partial | The page's own hit test over DevTools: a canvas the page cannot read and a cross-origin image in an iframe are not copied (C48). |
| `copyVideoFrameAt()` | — | missing |  |
| `saveVideoFrameAs()` | — | missing |  |
| `unselect()` | `Unselect` | partial | Script in the focused frame (C20). |
| `scrollToTop()` | `ScrollToTop` | partial | Script in the focused frame (C20). |
| `scrollToBottom()` | `ScrollToBottom` | partial | Script in the focused frame (C20). |
| `adjustSelection()` | `AdjustSelection` | partial | Script; `end` honoured, unlike v44 (C20, C21). |
| `replace()` | `Replace` | partial | Script and `execCommand` (C20). |
| `replaceMisspelling(text)` | `ReplaceMisspelling` | done |  |
| `insertText()` | `InsertTextAsync` | done | DevTools `Input.insertText`, an input-method commit as Electron's. |
| `findInPage(text, options)` | `FindInPage` | partial | A new session only when the text or case changes (C18). |
| `stopFindInPage(action)` | `StopFindInPage` | partial | `activateSelection` keeps the selection (C18). |
| `capturePage()` | `CapturePageAsync` | partial | `stayHidden`/`stayAwake` have no effect (C22). |
| `isBeingCaptured()` | — | missing |  |
| `getPrintersAsync()` | `GetPrintersAsync` | partial | Windows only, listed as Chromium lists them (Z4). |
| `print()` | `Print` | partial | Electron's option checks and device-name errors; opens Chromium's print dialog without the settings; no silent printing or outcome callback (Z1-Z3). |
| `printToPDF()` | `PrintToPDFAsync` | partial | Page.printToPDF over DevTools with Electron's defaults, validation messages and per-page queue; always the main frame, even with a selection in a focused iframe (C51). |
| `addWorkSpace()` | — | missing |  |
| `removeWorkSpace()` | — | missing |  |
| `setDevToolsWebContents()` | — | missing |  |
| `isDevToolsFocused()` | — | missing |  |
| `getDevToolsTitle()` | — | missing |  |
| `setDevToolsTitle()` | — | missing |  |
| `inspectElement()` | `InspectElement` | done | Which element DevTools selects is unverified: the selection is not visible from the page. |
| `inspectSharedWorker()` | — | missing |  |
| `inspectSharedWorkerById()` | — | missing |  |
| `getAllSharedWorkers()` | `GetAllSharedWorkers` | partial | Through DevTools' target discovery; a page's first call on the UI thread lists none (C54). |
| `inspectServiceWorker()` | — | missing |  |
| `sendToFrame()` | — | missing |  |
| `postMessage(channel, message, transfer)` | `PostMessage` | partial | JSON messages (I1). |
| `enableDeviceEmulation()` | `EnableDeviceEmulation` | partial | DevTools metrics override; `mobile` forces more (C24). |
| `disableDeviceEmulation()` | `DisableDeviceEmulation` | done |  |
| `beginFrameSubscription()` | — | missing |  |
| `endFrameSubscription()` | — | missing |  |
| `startDrag()` | `StartDrag` | partial | Windows (the drag blocks queued UI tasks, its image painted with GDI) and macOS (Electron's `NSDraggingSession`, _branch `mac-integration`_, its drag session starts on screen, macOS 26, arm64 and x64); not Linux (C40). |
| `savePage()` | `SavePageAsync` | partial | Through DevTools; `HTMLComplete` saves only the main document and its images, scripts and style sheets (C50). |
| `showDefinitionForSelection()` | `ShowDefinitionForSelection` | partial | macOS: Chromium's `ShowDictionaryOverlayForSelection` on the page's `RenderWidgetHostViewCocoa`, found under the browser's view; nothing elsewhere, as Electron _(branch `mac-integration`)_; not yet run on screen. |
| `isOffscreen()` | `IsOffscreen` | done | Always false: Riftway has no offscreen rendering (webPreferences `offscreen` is missing). |
| `startPainting()` | — | missing |  |
| `stopPainting()` | — | missing |  |
| `isPainting()` | — | missing |  |
| `setFrameRate()` | — | missing |  |
| `getFrameRate()` | — | missing |  |
| `invalidate()` | — | missing |  |
| `getWebRTCIPHandlingPolicy()` | `GetWebRtcIpHandlingPolicy` | partial | The value this page set; the session's pages share the setting (C52). |
| `setWebRTCIPHandlingPolicy()` | `SetWebRtcIpHandlingPolicy` | partial | Applies to every page of the session (C52). |
| `getWebRTCUDPPortRange()` | `GetWebRtcUdpPortRange` | partial | The range this page set; the session's pages share the setting (C52). |
| `setWebRTCUDPPortRange()` | `SetWebRtcUdpPortRange` | partial | Electron's checks and messages; applies to every page of the session (C52). |
| `getMediaSourceId()` | — | missing |  |
| `getOrCreateDevToolsTargetId()` | `GetOrCreateDevToolsTargetId` | partial | Read over DevTools as the page is created; `'` on the UI thread before that (C46). |
| `getOSProcessId()` | `GetOSProcessId` | done | 0 once the page has closed. |
| `getProcessId()` | `GetProcessId` | done | The main frame's, read from CEF's main frame; -1 once the page has closed. |
| `clone()` | — | missing |  |
| `takeHeapSnapshot()` | `TakeHeapSnapshotAsync` | done | DevTools `HeapProfiler.takeHeapSnapshot`, one at a time per page (C47). |
| `getBackgroundThrottling()` | — | missing | W62. |
| `setBackgroundThrottling()` | — | missing | W62. |
| `getType()` | `GetWebContentsType` | done | Renamed from .NET's `GetType` (C45); always `window`. |
| `setImageAnimationPolicy()` | `SetImageAnimationPolicy` | partial | Applies to every page of the session (C53). |
| `canGoBack()` | — | n/a | Deprecated; use navigationHistory. |
| `canGoForward()` | — | n/a | Deprecated; use navigationHistory. |
| `canGoToOffset()` | — | n/a | Deprecated; use navigationHistory. |
| `clearHistory()` | — | n/a | Deprecated; use navigationHistory. |
| `goBack()` | — | n/a | Deprecated; use navigationHistory. |
| `goForward()` | — | n/a | Deprecated; use navigationHistory. |
| `goToIndex()` | — | n/a | Deprecated; use navigationHistory. |
| `goToOffset()` | — | n/a | Deprecated; use navigationHistory. |

### Instance properties

| Electron | Riftway | Status | Note |
|---|---|---|---|
| `audioMuted` | `SetAudioMuted / IsAudioMuted` | done |  |
| `userAgent` | `SetUserAgent / GetUserAgent` | done |  |
| `zoomLevel` | `SetZoomLevel / GetZoomLevel` | done |  |
| `zoomFactor` | `SetZoomFactor / GetZoomFactor` | done |  |
| `navigationHistory` | `NavigationHistory` | done | Members in their own table below. |
| `mainFrame` | `MainFrame` | partial | Null until the frame tree has been read, just after window creation (F4). |
| `hostWebContents` | — | n/a | <webview> only. |
| `focusedFrame` | `FocusedFrame` | done | Not cross-site frames in another process (F3). |
| `ipc` | `Ipc` | done |  |
| `caretBrowsingEnabled` | `SetCaretBrowsingEnabled / IsCaretBrowsingEnabled` | partial | C53. |
| `frameRate` | — | missing |  |
| `id` | `Id` | done |  |
| `session` | `Session` | done | See DRIFT E1-E11 for the session gaps. |
| `devToolsWebContents` | — | missing |  |
| `debugger` | `Debugger` | done | Shares Riftway's protocol session (DRIFT C43) |
| `backgroundThrottling` | — | missing | W62. |
| `opener` | `Opener` | partial | Read over DevTools; on the UI thread, the value last read (C49). |
| `zoomMode` | — | missing |  |

### `navigationHistory` (`NavigationHistory`)

| Electron | Riftway | Status | Note |
|---|---|---|---|
| `canGoBack()` | `CanGoBack` | done |  |
| `canGoForward()` | `CanGoForward` | done |  |
| `canGoToOffset(offset)` | `CanGoToOffset` | done |  |
| `clear()` | `Clear` | done | Resets via DevTools; see N3. |
| `getActiveIndex()` | `GetActiveIndex` | done |  |
| `getEntryAtIndex(index)` | `GetEntryAtIndex` | done | Entry has `url` and `title` (no `pageState`, N1). |
| `goBack()` | `GoBack` | done |  |
| `goForward()` | `GoForward` | done |  |
| `goToIndex(index)` | `GoToIndex` | done | Directly, through DevTools |
| `goToOffset(offset)` | `GoToOffset` | done | Directly, through DevTools |
| `length()` | `Length` | done |  |
| `removeEntryAtIndex(index)` | — | missing | N1. |
| `getAllEntries()` | `GetAllEntries` | done |  |
| `restore(options)` | — | missing | N1. |

### `webFrameMain` statics and events (`WebFrameMain`)

| Electron | Riftway | Status | Note |
|---|---|---|---|
| `fromId(processId, routingId)` | — | missing | No routing id (F2). |
| `fromFrameToken(processId, frameToken)` | `WebFrameMain.FromFrameToken` | partial | Current frames of window pages, by CEF's frame identifier (F6). |
| `dom-ready (event)` | `DomReady` | done |  |

### `WebFrameMain` instance methods

| Electron | Riftway | Status | Note |
|---|---|---|---|
| `executeJavaScript(code, userGesture)` | `ExecuteJavaScriptAsync` | partial | JSON result only; cross-process frames cannot run script (F1, C6). |
| `reload()` | `Reload` | partial | Cross-process frames cannot reload (F1). |
| `isDestroyed()` | `IsDestroyed` | done |  |
| `send(channel, ...args)` | `Send` | partial | Dropped for cross-process frames and before the preload runs (F1). |
| `postMessage(channel, message, transfer)` | `WebFrameMain.PostMessage` | partial | JSON messages (I1). |
| `collectJavaScriptCallStack()` | — | missing | F5. |
| `copyVideoFrameAt(x, y)` | — | missing |  |
| `saveVideoFrameAs(x, y)` | — | missing |  |
| `printToPDF(options)` | `WebFrameMain.PrintToPdfAsync` | partial | The main frame only; a subframe throws (C51). |

### `WebFrameMain` instance properties

| Electron | Riftway | Status | Note |
|---|---|---|---|
| `url` | `Url` | done |  |
| `origin` | `Origin` | done |  |
| `top` | `Top` | done |  |
| `parent` | `Parent` | done |  |
| `frames` | `Frames` | done |  |
| `framesInSubtree` | `FramesInSubtree` | done |  |
| `name` | `Name` | done |  |
| `frameToken` | `FrameToken` | partial | Chromium's `LocalFrameToken`, from CEF's frame identifier; the frame is matched to CEF's (F6). |
| `detached` | `Detached` | done |  |
| `ipc` | `Ipc` | done | Kept per frame object across its navigations, where Electron's follows the frame tree node. |
| `frameTreeNodeId` | — | missing | F2. |
| `osProcessId` | `OsProcessId` | done | The child process launched with `--renderer-client-id` set to `processId`. |
| `processId` | `ProcessId` | partial | From CEF's frame identifier; the frame is matched to CEF's (F6). |
| `routingId` | — | missing | F2. |
| `visibilityState` | `VisibilityState` | partial | Reported by the document (F7). |
