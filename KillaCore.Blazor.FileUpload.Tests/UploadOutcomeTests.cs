using System.Net;
using KillaCore.Blazor.FileUpload.Client.Models;

namespace KillaCore.Blazor.FileUpload.Tests;

public class UploadOutcomeTests
{
    [Fact]
    public async Task Identical_content_in_one_batch_reports_a_duplicate()
    {
        await using var host = await UploadTestHost.StartAsync();

        var batchId = Guid.NewGuid().ToString();
        var payload = UploadTestHost.Pdf(4096, fill: 0x42);

        var first = await host.UploadAsync(payload, fileName: "a.pdf", batchId: batchId);
        var second = await host.UploadAsync(payload, fileName: "b.pdf", batchId: batchId);

        Assert.Equal("Saved", await UploadTestHost.OutcomeOf(first));
        Assert.Equal("DuplicateInBatch", await UploadTestHost.OutcomeOf(second));
        Assert.Single(host.Hooks.Saved);
    }

    [Fact]
    public async Task Content_already_stored_reports_a_remote_duplicate()
    {
        await using var host = await UploadTestHost.StartAsync();

        var payload = UploadTestHost.Pdf(4096, fill: 0x43);

        var first = await host.UploadAsync(payload, fileName: "a.pdf");
        var second = await host.UploadAsync(payload, fileName: "b.pdf"); // different batch

        Assert.Equal("Saved", await UploadTestHost.OutcomeOf(first));
        Assert.Equal("DuplicateOnServer", await UploadTestHost.OutcomeOf(second));
        Assert.Single(host.Hooks.Saved);
    }

    /// <summary>A duplicate is a 200 carrying an outcome, not an error — it renders as a skip.</summary>
    [Fact]
    public async Task A_duplicate_is_reported_as_success_not_failure()
    {
        await using var host = await UploadTestHost.StartAsync();

        var payload = UploadTestHost.Pdf(4096, fill: 0x44);
        await host.UploadAsync(payload, fileName: "a.pdf");

        var second = await host.UploadAsync(payload, fileName: "b.pdf");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task An_infected_file_is_rejected_and_never_saved()
    {
        var scanner = new StubScanner(clean: false, threat: "Eicar.Test");
        await using var host = await UploadTestHost.StartAsync(scanner: scanner);

        var response = await host.UploadAsync(UploadTestHost.Pdf(4096));

        response.EnsureSuccessStatusCode();
        Assert.Equal("Infected", await UploadTestHost.OutcomeOf(response));
        Assert.Contains("Eicar.Test", await response.Content.ReadAsStringAsync());
        Assert.Empty(host.Hooks.Saved);
        Assert.Equal(1, scanner.Calls);
    }

    [Fact]
    public async Task A_clean_verdict_lets_the_file_through()
    {
        var scanner = new StubScanner(clean: true);
        await using var host = await UploadTestHost.StartAsync(scanner: scanner);

        var response = await host.UploadAsync(UploadTestHost.Pdf(4096));

        Assert.Equal("Saved", await UploadTestHost.OutcomeOf(response));
        Assert.Single(host.Hooks.Saved);
        Assert.Equal(1, scanner.Calls);
    }

    /// <summary>
    /// Fail closed. Storing unscanned files precisely when the scanner is unreachable is the
    /// wrong default; a deployment that prefers otherwise catches inside its own scanner.
    /// </summary>
    [Fact]
    public async Task A_scanner_that_throws_rejects_the_upload()
    {
        var scanner = new StubScanner(clean: true, throws: true);
        await using var host = await UploadTestHost.StartAsync(scanner: scanner);

        var response = await host.UploadAsync(UploadTestHost.Pdf(4096));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Hooks.Saved);
    }

    [Fact]
    public async Task A_duplicate_is_not_scanned()
    {
        var scanner = new StubScanner(clean: true);
        await using var host = await UploadTestHost.StartAsync(scanner: scanner);

        var payload = UploadTestHost.Pdf(4096, fill: 0x45);
        await host.UploadAsync(payload, fileName: "a.pdf");
        await host.UploadAsync(payload, fileName: "b.pdf");

        // Scanned once: there is no point examining a file that will not be stored.
        Assert.Equal(1, scanner.Calls);
    }

    /// <summary>
    /// The completion payload is posted by the client, which never learns the server-computed
    /// hash, so DetectedHash used to arrive null in the batch hook. The server now keeps its own
    /// records and joins them on the file id carried in the signed token.
    /// </summary>
    [Fact]
    public async Task Batch_completion_reports_the_server_computed_hash()
    {
        await using var host = await UploadTestHost.StartAsync();

        var batchId = Guid.NewGuid().ToString();
        var fileId = Guid.NewGuid().ToString();

        await host.UploadAsync(UploadTestHost.Pdf(4096, fill: 0x46), batchId: batchId, fileId: fileId);

        // Exactly what the browser sends: no hash, because it has never seen one.
        var clientRecord = new FileTransferData
        {
            Id = fileId,
            FileName = "file.pdf",
            Status = TransferStatus.Completed,
            DetectedHash = null
        };

        var response = await host.CompleteBatchAsync(batchId, clientRecord);
        response.EnsureSuccessStatusCode();

        var reported = Assert.Single(host.Hooks.Batches).Files;
        var file = Assert.Single(reported);

        Assert.False(string.IsNullOrEmpty(file.DetectedHash));
        Assert.Equal(TransferStatus.Completed, file.Status);
        Assert.True(file.FileSize > 0);
    }

    /// <summary>
    /// A file the client rejected locally never reached the server, so the server has no record
    /// and the client's entry survives — correctly without a hash.
    /// </summary>
    [Fact]
    public async Task Batch_completion_keeps_client_records_the_server_never_saw()
    {
        await using var host = await UploadTestHost.StartAsync();

        var batchId = Guid.NewGuid().ToString();
        await host.UploadAsync(UploadTestHost.Pdf(4096, fill: 0x47), batchId: batchId);

        var skippedLocally = new FileTransferData
        {
            Id = Guid.NewGuid().ToString(),
            FileName = "too-big.pdf",
            Status = TransferStatus.Skipped
        };

        await host.CompleteBatchAsync(batchId, skippedLocally);

        var reported = Assert.Single(host.Hooks.Batches).Files;

        Assert.Contains(reported, f => f.FileName == "too-big.pdf" && f.DetectedHash is null);
        Assert.Contains(reported, f => f.DetectedHash is not null);
    }

    /// <summary>
    /// The batch is registered as soon as a valid token is presented, so a batch whose files all
    /// failed validation can still report. Previously the tracker had no record of it and the
    /// completion call returned 400.
    /// </summary>
    [Fact]
    public async Task A_batch_whose_files_all_failed_can_still_complete()
    {
        await using var host = await UploadTestHost.StartAsync();

        var batchId = Guid.NewGuid().ToString();

        var upload = await host.UploadAsync(UploadTestHost.NotAPdf(), fileName: "forged.pdf", batchId: batchId);
        Assert.Equal(HttpStatusCode.BadRequest, upload.StatusCode);

        var completion = await host.CompleteBatchAsync(batchId, new FileTransferData
        {
            Id = Guid.NewGuid().ToString(),
            FileName = "forged.pdf",
            Status = TransferStatus.Failed
        });

        completion.EnsureSuccessStatusCode();
        Assert.Single(host.Hooks.Batches);
    }

    [Fact]
    public async Task A_batch_cannot_be_completed_twice()
    {
        await using var host = await UploadTestHost.StartAsync();

        var batchId = Guid.NewGuid().ToString();
        await host.UploadAsync(UploadTestHost.Pdf(4096, fill: 0x48), batchId: batchId);

        var first = await host.CompleteBatchAsync(batchId);
        var second = await host.CompleteBatchAsync(batchId);

        first.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task An_unknown_batch_cannot_be_completed()
    {
        await using var host = await UploadTestHost.StartAsync();

        var response = await host.CompleteBatchAsync(Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Direct mode holds nothing, so there is no "before the save" in which to check — identical
    /// content is stored twice. Documented behaviour, asserted so it stays deliberate.
    /// </summary>
    [Fact]
    public async Task Direct_mode_stores_identical_content_twice()
    {
        await using var host = await UploadTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["KillaCoreFileUpload:StreamDirectToStorage"] = "true"
        });

        var payload = UploadTestHost.Pdf(4096, fill: 0x49);

        var first = await host.UploadAsync(payload, fileName: "a.pdf");
        var second = await host.UploadAsync(payload, fileName: "b.pdf");

        Assert.Equal("Saved", await UploadTestHost.OutcomeOf(first));
        Assert.Equal("Saved", await UploadTestHost.OutcomeOf(second));
        Assert.Equal(2, host.Hooks.Saved.Count);
    }

    [Fact]
    public async Task Direct_mode_still_computes_the_hash_and_size()
    {
        await using var host = await UploadTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["KillaCoreFileUpload:StreamDirectToStorage"] = "true"
        });

        var payload = UploadTestHost.Pdf(8192, fill: 0x4A);
        await host.UploadAsync(payload);

        var saved = Assert.Single(host.Hooks.Saved);

        // Available only after the hook drained the stream, which is why they are read afterwards.
        Assert.False(string.IsNullOrEmpty(saved.DetectedHash));
        Assert.Equal(payload.Length, saved.FileSize);
    }

    [Fact]
    public async Task The_hook_receives_the_exact_bytes_that_were_uploaded()
    {
        await using var host = await UploadTestHost.StartAsync();

        var payload = UploadTestHost.Pdf(20_000, fill: 0x4B);
        await host.UploadAsync(payload, fileName: "exact.pdf");

        Assert.Equal(payload, host.Hooks.SavedBytes["exact.pdf"]);
    }
}
