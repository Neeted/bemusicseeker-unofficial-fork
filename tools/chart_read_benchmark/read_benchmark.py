import os
import time
import sys

LIST_PATH = r"D:\work\BeMusicSeeker-decomp\tools\chart_read_benchmark\chart_list.txt"

# 1MiB〜16MiBあたりで比較するとよいです
BUFFER_SIZE = 256 * 1024 * 1024

# file_list.txt が UTF-8 BOM付き/なしなら utf-8-sig
# cmd.exe の dir /s /b などで作ったCP932系なら "mbcs" に変更
LIST_ENCODING = "utf-8-sig"
# LIST_ENCODING = "mbcs"

def open_binary_sequential(path: str):
    """
    Windowsでは O_SEQUENTIAL が使える場合、順次読み取りヒントを付ける。
    使えない環境では通常の open 相当になる。
    """
    flags = os.O_RDONLY

    if hasattr(os, "O_BINARY"):
        flags |= os.O_BINARY

    if hasattr(os, "O_SEQUENTIAL"):
        flags |= os.O_SEQUENTIAL

    fd = os.open(path, flags)
    return os.fdopen(fd, "rb", buffering=0)

def main():
    buffer = bytearray(BUFFER_SIZE)
    view = memoryview(buffer)

    total_bytes = 0
    file_count = 0
    error_count = 0
    errors = []

    start = time.perf_counter()

    with open(LIST_PATH, "r", encoding=LIST_ENCODING) as list_file:
        for line in list_file:
            path = line.rstrip("\r\n")

            if not path:
                continue

            try:
                with open_binary_sequential(path) as f:
                    while True:
                        n = f.readinto(view)
                        if not n:
                            break
                        total_bytes += n

                file_count += 1

                # 進捗表示は出しすぎると測定に影響するので控えめに
                if file_count % 5000 == 0:
                    elapsed = time.perf_counter() - start
                    gib = total_bytes / (1024 ** 3)
                    speed = gib / elapsed if elapsed > 0 else 0
                    print(
                        f"{file_count:,} files, {gib:.2f} GiB, {speed:.2f} GiB/s",
                        file=sys.stderr,
                    )

            except Exception as e:
                error_count += 1
                if len(errors) < 20:
                    errors.append((path, repr(e)))

    elapsed = time.perf_counter() - start
    mib = total_bytes / (1024 ** 2)
    gib = total_bytes / (1024 ** 3)

    print("---- result ----")
    print(f"files       : {file_count:,}")
    print(f"errors      : {error_count:,}")
    print(f"bytes       : {total_bytes:,}")
    print(f"size        : {mib:.2f} MiB / {gib:.2f} GiB")
    print(f"elapsed     : {elapsed:.3f} sec")
    print(f"throughput  : {mib / elapsed:.2f} MiB/s")
    print(f"throughput  : {gib / elapsed:.2f} GiB/s")

    if errors:
        print("---- first errors ----")
        for path, err in errors:
            print(path)
            print("  ", err)

if __name__ == "__main__":
    main()