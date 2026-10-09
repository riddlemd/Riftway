# DownloadItem parity

Against Electron **v44.5.1** (`docs/api/download-item.md`, `shell/browser/api/electron_api_download_item.cc`, the
`will-download`, `downloadURL` and `createInterruptedDownload` parts of `docs/api/session.md` and
`electron_api_session.cc`, and `electron_download_manager_delegate.cc`). Audited 2026-10-09; update this file as items land.

Status: **done**, **partial** (exists with a documented gap; DRIFT.md id in the note), **missing**, or **n/a**.

## Summary

Percentages are over the non-n/a items; the second counts a partial item as half.

| Area | Items | Done | Partial | Missing | n/a | % done | % done + partial/2 |
|---|---|---|---|---|---|---|---|
| Instance events | 2 | 2 | 0 | 0 | 0 | 100.0% | 100.0% |
| Instance methods | 25 | 17 | 7 | 1 | 0 | 68.0% | 82.0% |
| Instance properties | 1 | 1 | 0 | 0 | 0 | 100.0% | 100.0% |
| Session download API | 3 | 0 | 2 | 1 | 0 | 0.0% | 33.3% |
| **Total** | 31 | 20 | 9 | 2 | 0 | 64.5% | 79.0% |

## Checklist

### Instance events

| Electron | Oriel | Status | Note |
|---|---|---|---|
| `updated` | `Updated` | done | `progressing`, or `interrupted` when Chromium's resume-mode table says it can resume (`IsDownloadDone`, ported over the interrupt reason and URL). |
| `done` | `Done` | done | Once: `completed`, `cancelled`, or an `interrupted` it cannot resume from. |

### Instance methods

| Electron | Oriel | Status | Note |
|---|---|---|---|
| `setSavePath(path)` | `SetSavePath` | done | Stored whenever called, honoured only during `WillDownload`, as Electron; missing directories are made. |
| `getSavePath()` | `GetSavePath` | done | Empty until set or chosen in the dialog. |
| `setSaveDialogOptions(options)` | `SetSaveDialogOptions` | done | The dialog is titled with the URL and starts at the generated path when they are empty; on Windows, no filters gives the extension's (described from the registry, else "EXT File") plus All Files, as `FormatFilterForExtensions`. Linux dialog limits are G1-G8. |
| `getSaveDialogOptions()` | `GetSaveDialogOptions` | done |  |
| `pause()` | `Pause` | done | A pause asked during `WillDownload` waits for the save path, as Chromium's target-pending pause. |
| `isPaused()` | `IsPaused` | done | From CEF's latest report. |
| `resume()` | `Resume` | done | Resumes a paused download, or an interrupted one through CEF's callback (Chromium's `Resume(true)`). |
| `canResume()` | `CanResume` | partial | Chromium's `CanResume` without the automatic-resumption count (E14). |
| `cancel()` | `Cancel` | partial | An interrupted download cannot be cancelled (E39). |
| `getURL()` | `GetUrl` | done |  |
| `getMimeType()` | `GetMimeType` | done |  |
| `hasUserGesture()` | `HasUserGesture` | partial | Always false (E13). |
| `getInitiatorOrigin()` | — | missing | E40. |
| `getFilename()` | `GetFilename` | partial | The generated name, which the dialog's choice does not change, as Electron; charset fallback differs (E41). |
| `getCurrentBytesPerSecond()` | `GetCurrentBytesPerSecond` | done |  |
| `getTotalBytes()` | `GetTotalBytes` | done | 0 when unknown. |
| `getReceivedBytes()` | `GetReceivedBytes` | done |  |
| `getPercentComplete()` | `GetPercentComplete` | done | -1 when the size is unknown, as Chromium's `PercentComplete`. |
| `getContentDisposition()` | `GetContentDisposition` | done |  |
| `getState()` | `GetState` | done | `DownloadState`. |
| `getURLChain()` | `GetUrlChain` | partial | Original and final URLs only (E15). |
| `getLastModifiedTime()` | `GetLastModifiedTime` | partial | Always empty (E13). |
| `getETag()` | `GetETag` | partial | Always empty (E13). |
| `getStartTime()` | `GetStartTime` | done | Unix seconds. |
| `getEndTime()` | `GetEndTime` | done | 0 until the download ends. |

### Instance properties

| Electron | Oriel | Status | Note |
|---|---|---|---|
| `savePath` | `SavePath` | done |  |

### Session download API

| Electron | Oriel | Status | Note |
|---|---|---|---|
| `will-download` | `Session.WillDownload` | partial | `Cancel` is `preventDefault()` and the item reports `cancelled`; `WebContents` is null for `Session.DownloadUrl`; no `frame` (E40). |
| `ses.downloadURL(url[, options])` | `Session.DownloadUrl` | partial | `headers` honoured; started from a hidden page (E37). |
| `ses.createInterruptedDownload(options)` | — | missing | E16. |
