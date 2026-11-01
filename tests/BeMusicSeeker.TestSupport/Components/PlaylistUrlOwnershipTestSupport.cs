using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Net;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>同じ機能の複数moduleが必要とする入力・観測だけを共用します。</summary>
internal static class PlaylistUrlOwnershipTestSupport
{

    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal sealed class StagedBodyStream : Stream
    {
        private int next;
        internal TaskCompletionSource[] Started { get; } = [.. Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];
        internal TaskCompletionSource<int>[] Release { get; } = [.. Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously))];
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool IsDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int index = next++;
            Started[index].TrySetResult();
            try
            {
                int count = await Release[index].Task.WaitAsync(cancellationToken);
                buffer.Span[..count].Fill((byte)' ');
                return count;
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                await Terminal.Task;
                throw;
            }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal sealed class RecordingPlaylistUrlDialogService : IUiDialogService
    {
        internal List<UiConfirmationRequest> Confirmations { get; } = [];

        internal List<UiMessageRequest> Messages { get; } = [];

        internal TaskCompletionSource<UiDialogResult>? PendingConfirmation { get; set; }

        internal UiDialogResult ConfirmationResult { get; set; } =
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal UiDialogResult MessageResult { get; set; } =
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal Action<UiMessageRequest>? MessageObserver { get; set; }

        public Task<UiDialogResult> ShowMessageAsync(
            UiMessageRequest request,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(request);
            MessageObserver?.Invoke(request);
            return Task.FromResult(MessageResult);
        }

        public Task<UiDialogResult> ConfirmAsync(
            UiConfirmationRequest request,
            CancellationToken cancellationToken = default)
        {
            Confirmations.Add(request);
            return PendingConfirmation?.Task
                ?? Task.FromResult(ConfirmationResult);
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(
            UiFilePickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(
            UiFolderPickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(
            UiSaveFilePickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal sealed class RecordingPlaylistUrlDownloadGateway : IPlaylistUrlDownloadGateway
    {
        private readonly string temporaryDirectory;

        private readonly Dictionary<string, Func<AppHttpResponse>> responses =
            new(StringComparer.Ordinal);

        internal RecordingPlaylistUrlDownloadGateway(string temporaryDirectory)
        {
            this.temporaryDirectory = temporaryDirectory;
        }

        internal List<Uri> RequestedUris { get; } = [];

        internal int OpenWriteCount { get; private set; }

        internal int GetTemporaryDirectoryCount { get; private set; }

        internal Exception? TransportException { get; set; }

        internal Func<Uri, AppHttpResponse>? ResponseFactory { get; set; }

        internal Exception? DeleteFailure { get; set; }

        internal int DeleteCount { get; private set; }

        internal Action? BeforeDelete { get; set; }

        internal void AddResponse(Uri uri, Func<AppHttpResponse> responseFactory)
        {
            responses[uri.AbsoluteUri] = responseFactory;
        }

        public Task<AppHttpResponse> OpenReadAsync(Uri uri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedUris.Add(uri);
            if (TransportException is not null)
            {
                throw TransportException;
            }
            if (ResponseFactory != null)
            {
                return Task.FromResult(ResponseFactory(uri));
            }
            if (!responses.TryGetValue(uri.AbsoluteUri, out Func<AppHttpResponse>? responseFactory))
            {
                throw new InvalidOperationException("No fake response was configured for " + uri);
            }
            return Task.FromResult(responseFactory());
        }

        public string GetTemporaryDirectory()
        {
            GetTemporaryDirectoryCount++;
            return temporaryDirectory;
        }

        public FileStream OpenWrite(string path, FileMode mode, FileAccess access, FileShare share)
        {
            OpenWriteCount++;
            return new FileStream(path, mode, access, share);
        }

        public bool FileExists(string path) => File.Exists(path);

        public void DeleteFile(string path)
        {
            DeleteCount++;
            BeforeDelete?.Invoke();
            if (DeleteFailure != null) { throw DeleteFailure; }
            File.Delete(path);
        }
    }


    /// <remarks>CoreとWindowsで同じ取得候補・行入力を共用し、順序・default・応答内容を維持します。</remarks>
    internal sealed class BatchHandoffLookupProvider(IReadOnlyDictionary<string, Uri> downloadUris) : IPlaylistExternalPackageLookupProvider
    {
        public string ProviderId => "batch-handoff-test";

        internal List<string> RequestedMd5s { get; } = [];

        public Task<PlaylistExternalPackageLookupResult> LookupAsync(string chartMd5, CancellationToken cancellationToken)
        {
            RequestedMd5s.Add(chartMd5);
            return Task.FromResult(new PlaylistExternalPackageLookupResult(ProviderId, chartMd5, downloadUris[chartMd5]));
        }
    }



    /// <remarks>CoreとWindowsで同じ取得候補・行入力を共用し、順序・default・応答内容を維持します。</remarks>
    internal static PlaylistDetailRow CreatePlaylistUrlRow(string? url, string? diffUrl)
        => new PlaylistDetailSourceRow(CreatePlaylistEntry(string.Empty, url, diffUrl), resolvedChart: null).CreateViewRow();

    /// <remarks>CoreとWindowsで同じ取得候補・行入力を共用し、順序・default・応答内容を維持します。</remarks>
    internal static PlaylistDetailRow CreatePlaylistExternalPackageRow(string md5)
        => new PlaylistDetailSourceRow(CreatePlaylistEntry(md5), resolvedChart: null).CreateViewRow();

    /// <remarks>CoreとWindowsで同じ取得候補・行入力を共用し、順序・default・応答内容を維持します。</remarks>
    internal static BMSTableEntry CreatePlaylistEntry(string md5, string? url = null, string? diffUrl = null)
    {
        return BMSTableEntry.CreateHydratedPlaylistEntry(
            playlistId: 1, md5Value: md5, sha256Value: null, levelValue: null,
            titleValue: "Playlist Entry", artistValue: "Artist", folderValue: string.Empty,
            lr2BmsIdValue: string.Empty, urlValue: url ?? string.Empty, urlDiffValue: diffUrl ?? string.Empty,
            nameDiffValue: string.Empty, orgMd5Value: string.Empty, addDateValue: null,
            commentValue: string.Empty, memoValue: string.Empty, isRemovedValue: false);
    }
}
