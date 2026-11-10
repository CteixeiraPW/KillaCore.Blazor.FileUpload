using KillaCore.Blazor.FileUpload.Client.Models;
using KillaCore.Blazor.FileUpload.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace KillaCore.Blazor.FileUpload.Filters;

/// <summary>
/// Rejects an over-sized upload before model binding reads the body.
/// <para>
/// Without this the size limit is a late complaint rather than a limit: binding an
/// <c>IFormFile</c> buffers the whole request to disk, so <c>file.Length</c> only exists once
/// every byte has already been transferred. A 400MB file against a 50MB profile would cost the
/// user the full upload and then receive an error.
/// </para>
/// <para>
/// A resource filter runs around model binding, which is the last point where the body is still
/// untouched. <c>Content-Length</c> is advisory — absent on chunked requests, and it counts
/// multipart framing as well as the file — so this is strictly an early-out; the controller
/// re-checks the real file length as the authoritative test.
/// </para>
/// </summary>
internal sealed class RequestSizeGuardFilter(IOptions<FileUploadServerOptions> options) : IAsyncResourceFilter
{
    /// <summary>Slack for the multipart boundary, part headers and trailer.</summary>
    private const long MultipartFramingAllowance = 8 * 1024;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var request = context.HttpContext.Request;

        var uploadContext = request.Headers[FileUploadConstants.CONTEXT_HEADER_NAME].FirstOrDefault();
        var limits = options.Value.GetEffectiveLimits(uploadContext);

        long? declaredLength = request.ContentLength;

        if (declaredLength > limits.MaxFileBytes + MultipartFramingAllowance)
        {
            context.Result = new ObjectResult(
                $"File exceeds the {limits.MaxFileBytes:N0} byte limit for this upload.")
            {
                StatusCode = StatusCodes.Status413PayloadTooLarge
            };
            return;
        }

        await next();
    }
}
