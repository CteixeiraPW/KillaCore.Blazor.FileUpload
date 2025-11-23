/**
 * @param {object} dotNetHelper - Reference to C# component to report progress
 */
export function uploadFile(dotNetHelper, inputSelector, fileIndex, url, barchId, token) {
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
        xhr.setRequestHeader("X-Upload-Token", token);
        xhr.setRequestHeader("X-Requested-With", "XMLHttpRequest");

        // --- NEW: PROGRESS REPORTING ---
        let lastReportTime = 0;

        xhr.upload.onprogress = function (e) {
            if (e.lengthComputable) {
                const now = Date.now();
                // Throttle: Only notify C# every 200ms OR if complete
                if (now - lastReportTime > 200 || e.loaded === e.total) {
                    lastReportTime = now;
                    const percent = (e.loaded / e.total) * 100;

                    // Call C# method 'ReportUploadProgress'
                    // We don't await this because we don't want to slow down the upload loop
                    dotNetHelper.invokeMethodAsync('ReportUploadProgress', batchId, fileIndex, percent);
                }
            }
        };

        xhr.onload = function () {
            if (xhr.status >= 200 && xhr.status < 300) {
                try {
                    const response = JSON.parse(xhr.responseText);
                    const path = response.tempPath || response.TempPath;
                    if (path) resolve(path);
                    else reject("No TempPath returned");
                } catch (e) { reject(e.message); }
            } else {
                reject(`Status: ${xhr.status}`);
            }
        };

        xhr.onerror = () => reject("Network Error");
        xhr.onabort = () => reject("Cancelled");

        xhr.send(formData);
    });
}