namespace BeMusicSeeker.Models;

internal enum PlaylistSyncStatusKind
{
    None = 0,
    Ok,
    Updated,
    HeaderNotFound,
    HeaderParseError,
    DataParseError,
    InvalidDataUrl,
    Http404,
    Http403,
    HttpError,
    NetworkError,
    UnknownError
}
