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
    policyHeaderName
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
        xhr.open("POST", url, true);

        // --- CHANGED: Dynamic Header Names & Policy Injection ---
        xhr.setRequestHeader(tokenHeaderName, uploadToken);
        xhr.setRequestHeader(policyHeaderName, policyToken);
        xhr.setRequestHeader("X-Requested-With", "XMLHttpRequest");

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
                    const claimToken = response.token;

                    if (claimToken) resolve(claimToken);
                    else reject("No Claim Token returned from server");
                } catch (e) {
                    // This catches the case where the server returns non-JSON (like the HTML error)
                    // even if the status code was 200 (rare) or another error code.
                    console.error("Server returned invalid JSON:", xhr.responseText.substring(0, 100));
                    reject("Server returned an invalid response format.");
                }
            } else {
                // Handle other HTTP errors
                reject(`Upload failed. Status: ${xhr.status}`);
            }
        };

        xhr.onerror = () => reject("Network Error");
        xhr.onabort = () => reject("Cancelled");

        xhr.send(formData);
    });
}