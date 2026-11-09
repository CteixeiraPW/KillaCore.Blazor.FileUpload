/**
 * In-flight XMLHttpRequests, keyed by "batchId:fileIndex".
 *
 * Blazor's JS interop CancellationToken only abandons the .NET side of the await — it has no
 * channel to signal JavaScript. Without this registry a "cancelled" upload keeps streaming
 * every byte of the file to the server, the server never sees the client give up, and the
 * server-side CancellationToken never fires.
 */
const _inFlight = new Map();

const transferKey = (batchId, fileIndex) => `${batchId}:${fileIndex}`;

/**
 * Aborts one in-flight transfer. Safe to call for a transfer that already finished, was
 * already aborted, or never started.
 * @param {string} batchId - Batch the transfer belongs to
 * @param {number} fileIndex - Index in the DOM FileList
 */
export function cancelUpload(batchId, fileIndex) {
    const xhr = _inFlight.get(transferKey(batchId, fileIndex));
    if (xhr) {
        xhr.abort();
    }
}

/**
 * Aborts every in-flight transfer belonging to a batch. Used on component disposal, where
 * awaiting one call is more deterministic than relying on per-file cancellation callbacks
 * that would race the teardown of this module.
 * @param {string} batchId - Batch to abort
 * @returns {number} how many transfers were aborted
 */
export function cancelBatch(batchId) {
    const prefix = `${batchId}:`;
    let aborted = 0;

    // Snapshot the keys first: abort() fires loadend synchronously, which mutates the map.
    for (const key of [..._inFlight.keys()]) {
        if (key.startsWith(prefix)) {
            _inFlight.get(key)?.abort();
            aborted++;
        }
    }

    return aborted;
}

/**
 * @param {object} dotNetHelper - Reference to C# component to report progress
 * @param {string} inputSelector - DOM ID
 * @param {int} fileIndex - Index in file list
 * @param {string} url - API Endpoint
 * @param {string} batchId - Current Batch ID
 * @param {string} uploadToken - The Anti-Replay/Auth Token (X-Upload-Token)
 * @param {string} tokenHeaderName - Name of the Auth Header
 * @param {string} policyToken - The Encrypted Allowed Extensions (X-Upload-Policy)
 * @param {string} policyHeaderName - Name of the Policy Header
 */
export function uploadFile(
    dotNetHelper,
    inputSelector,
    fileIndex,
    url,
    batchId,
    uploadToken,
    tokenHeaderName,
    policyToken,
    policyHeaderName,
    uploadContext
) {
    return new Promise((resolve, reject) => {
        const input = document.querySelector(inputSelector);
        if (!input || !input.files || !input.files[fileIndex]) {
            reject("File not found");
            return;
        }

        const file = input.files[fileIndex];
        const formData = new FormData();
        formData.append("file", file);

        const xhr = new XMLHttpRequest();

        // Register before send() so a cancellation that arrives immediately still finds it.
        // loadend fires for load, error, abort and timeout alike, so the map cannot leak an
        // entry per file over a long-lived session.
        const key = transferKey(batchId, fileIndex);
        _inFlight.set(key, xhr);
        xhr.onloadend = () => _inFlight.delete(key);

        xhr.open("POST", url, true);

        // --- CHANGED: Dynamic Header Names, Policy Injection, and Batch ID ---
        xhr.setRequestHeader(tokenHeaderName, uploadToken);
        xhr.setRequestHeader(policyHeaderName, policyToken);
        xhr.setRequestHeader("X-Batch-Id", batchId);
        xhr.setRequestHeader("X-Requested-With", "XMLHttpRequest");

        if (uploadContext) {
            xhr.setRequestHeader("X-Upload-Context", uploadContext);
        }

        // --- Progress Reporting (Unchanged) ---
        let lastReportTime = 0;
        xhr.upload.onprogress = function (e) {
            if (e.lengthComputable) {
                const now = Date.now();
                if (now - lastReportTime > 200 || e.loaded === e.total) {
                    lastReportTime = now;
                    const percent = (e.loaded / e.total) * 100;
                    dotNetHelper.invokeMethodAsync('ReportUploadProgress', batchId, fileIndex, percent);
                }
            }
        };

        xhr.onload = function () {
            // 1. Check for specific IIS/Server errors first
            if (xhr.status === 413) {
                reject("Upload Blocked (413). If you are using IIS, you must increase 'maxAllowedContentLength' in web.config.");
                return;
            }

            if (xhr.status >= 200 && xhr.status < 300) {
                try {
                    const response = JSON.parse(xhr.responseText);

                    if (response.token) resolve(response);
                    else reject("No Claim Token returned from server");
                } catch (e) {
                    // This catches the case where the server returns non-JSON (like the HTML error)
                    // even if the status code was 200 (rare) or another error code.
                    console.error("Server returned invalid JSON:", xhr.responseText.substring(0, 100));
                    reject("Server returned an invalid response format.");
                }
            } else {
                // Handle other HTTP errors
                let errorMessage = `Upload failed (Status ${xhr.status})`;

                if (xhr.responseText) {
                    // Try to parse as JSON (for ProblemDetails or API errors)
                    try {
                        const errorJson = JSON.parse(xhr.responseText);

                        // Look for standard ProblemDetails fields or custom messages
                        if (errorJson.detail) {
                            errorMessage = errorJson.detail;
                        } else if (errorJson.title) {
                            errorMessage = errorJson.title;
                        } else if (errorJson.message) {
                            errorMessage = errorJson.message;
                        } else {
                            // If it's just a raw JSON object, take a snippet
                            errorMessage += `: ${JSON.stringify(errorJson).substring(0, 200)}`;
                        }
                    } catch (e) {
                        // If not JSON, it's likely a plain text error from BadRequest("String")
                        // Clean up the text (remove quotes if API returns string literal)
                        const cleanText = xhr.responseText.replace(/^"|"$/g, '');
                        if (cleanText.length > 0) {
                            errorMessage = cleanText;
                        }
                    }
                }
                reject(errorMessage);
            }
        };

        xhr.onerror = () => reject("Network Error");
        xhr.onabort = () => reject("Cancelled");

        xhr.send(formData);
    });
}
