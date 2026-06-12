# /// script
# requires-python = ">=3.12"
# dependencies = [
#   "beautifulsoup4>=4.12,<5",
#   "markdown>=3.7,<4",
#   "pygments>=2.18,<3",
#   "pymdown-extensions>=10.14,<11",
# ]
# ///

from __future__ import annotations

import argparse
import html
import posixpath
import re
import shutil
import sys
from pathlib import Path, PurePosixPath
from urllib.parse import unquote, urlsplit, urlunsplit

import markdown
from bs4 import BeautifulSoup


DEFAULT_DOCUMENTS = (
    "README.ja.md",
    "README.md",
    "docs/manual.ja.md",
    "docs/keyword-search-syntax-guide.md",
    "docs/log-level-info-guide.md",
    "docs/sjis-path-validation-note.md",
)

NAV_LABELS = {
    "README.ja.md": "README (日本語)",
    "README.md": "README (English)",
    "docs/manual.ja.md": "ユーザーマニュアル",
    "docs/keyword-search-syntax-guide.md": "キーワード検索構文ガイド",
    "docs/log-level-info-guide.md": "ログレベル INFO ガイド",
    "docs/sjis-path-validation-note.md": "Shift_JIS パス注意",
}

EXTERNAL_SCHEMES = {
    "http",
    "https",
    "mailto",
    "tel",
    "ftp",
    "file",
}

CSS = r"""
:root {
  color-scheme: light;
  --page-bg: #f5f7fa;
  --content-bg: #ffffff;
  --text: #242933;
  --muted: #657083;
  --border: #d9e0e8;
  --border-soft: #e9edf2;
  --accent: #0f766e;
  --accent-strong: #0b5f59;
  --code-bg: #f3f5f7;
  --pre-bg: #17202e;
  --pre-text: #edf2f7;
  --table-head: #f0f5f5;
  --shadow: 0 18px 38px rgba(31, 41, 55, 0.10);
}

* {
  box-sizing: border-box;
}

html {
  scroll-padding-top: 24px;
}

body {
  margin: 0;
  background: var(--page-bg);
  color: var(--text);
  font-family: "Segoe UI", "Meiryo", "Yu Gothic UI", system-ui, sans-serif;
  font-size: 16px;
  line-height: 1.72;
}

a {
  color: var(--accent);
  text-decoration-thickness: 1px;
  text-underline-offset: 0.16em;
}

a:hover {
  color: var(--accent-strong);
}

.page-shell {
  display: grid;
  grid-template-columns: minmax(200px, 260px) minmax(0, 920px);
  gap: 28px;
  max-width: 1240px;
  margin: 0 auto;
  padding: 32px 24px;
}

.doc-nav {
  align-self: start;
  position: sticky;
  top: 24px;
  padding: 16px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--content-bg);
}

.doc-nav-title {
  margin: 0 0 10px;
  color: var(--muted);
  font-size: 12px;
  font-weight: 700;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.doc-nav a {
  display: block;
  margin: 4px 0;
  padding: 7px 9px;
  border-radius: 6px;
  color: var(--text);
  line-height: 1.42;
  text-decoration: none;
}

.doc-nav a:hover {
  background: #eef5f4;
  color: var(--accent-strong);
}

.doc-nav a[aria-current="page"] {
  background: #dff0ed;
  color: var(--accent-strong);
  font-weight: 700;
}

.doc-content {
  min-width: 0;
  padding: 44px 54px 56px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--content-bg);
  box-shadow: var(--shadow);
}

h1,
h2,
h3,
h4 {
  line-height: 1.32;
}

h1 {
  margin: 0 0 22px;
  padding-bottom: 18px;
  border-bottom: 1px solid var(--border);
  font-size: 2.0rem;
}

h2 {
  margin-top: 44px;
  padding-bottom: 8px;
  border-bottom: 1px solid var(--border-soft);
  font-size: 1.45rem;
}

h3 {
  margin-top: 32px;
  font-size: 1.18rem;
}

h4 {
  margin-top: 26px;
  font-size: 1.05rem;
}

p,
ul,
ol,
table,
pre,
blockquote {
  margin-top: 0;
  margin-bottom: 18px;
}

ul,
ol {
  padding-left: 1.55em;
}

li + li {
  margin-top: 4px;
}

blockquote {
  padding: 12px 16px;
  border-left: 4px solid var(--accent);
  background: #f4faf8;
  color: #3b4656;
}

code {
  padding: 0.12em 0.34em;
  border-radius: 4px;
  background: var(--code-bg);
  font-family: "Cascadia Mono", "Consolas", monospace;
  font-size: 0.92em;
  overflow-wrap: anywhere;
}

pre {
  overflow-x: auto;
  padding: 16px 18px;
  border-radius: 8px;
  background: var(--pre-bg);
  color: var(--pre-text);
}

pre code {
  padding: 0;
  background: transparent;
  color: inherit;
  font-size: 0.93em;
}

.table-scroll {
  width: 100%;
  overflow-x: auto;
  margin-bottom: 18px;
}

table {
  width: 100%;
  min-width: 560px;
  margin-bottom: 0;
  border-collapse: collapse;
  border-spacing: 0;
}

th,
td {
  padding: 9px 11px;
  border: 1px solid var(--border);
  vertical-align: top;
}

th {
  background: var(--table-head);
  font-weight: 700;
}

tr:nth-child(even) td {
  background: #fbfcfd;
}

img {
  max-width: 100%;
  height: auto;
}

.doc-content > p > img,
.doc-content > p > a > img {
  display: inline-block;
}

.doc-content p:has(> img:only-child),
.doc-content p:has(> a > img:only-child) {
  margin: 24px 0;
}

.doc-content p:has(> img:only-child) img,
.doc-content p:has(> a > img:only-child) img {
  display: block;
  border: 1px solid var(--border);
  border-radius: 6px;
}

hr {
  border: 0;
  border-top: 1px solid var(--border);
  margin: 32px 0;
}

@media (max-width: 900px) {
  .page-shell {
    display: block;
    padding: 18px;
  }

  .doc-nav {
    position: static;
    margin-bottom: 18px;
  }

  .doc-content {
    padding: 28px 22px 36px;
  }

  h1 {
    font-size: 1.7rem;
  }
}
"""


def to_posix(path: str | Path | PurePosixPath) -> str:
    return str(path).replace("\\", "/")


def normalize_doc_path(path: str | PurePosixPath) -> PurePosixPath:
    value = to_posix(path).strip("/")
    return PurePosixPath(posixpath.normpath(value))


def html_path_for(markdown_path: PurePosixPath) -> PurePosixPath:
    name = markdown_path.name
    if name.endswith(".md"):
        name = name[:-3] + ".html"
    return markdown_path.with_name(name)


def relative_url(from_html: PurePosixPath, to_html: PurePosixPath) -> str:
    rel = posixpath.relpath(to_html.as_posix(), start=from_html.parent.as_posix() or ".")
    return "." if rel == "." else rel


def is_external_href(href: str) -> bool:
    parsed = urlsplit(href)
    return bool(parsed.scheme and parsed.scheme.lower() in EXTERNAL_SCHEMES) or bool(parsed.netloc)


def render_markdown(source: str) -> str:
    md = markdown.Markdown(
        extensions=[
            "markdown.extensions.extra",
            "markdown.extensions.sane_lists",
            "markdown.extensions.toc",
            "pymdownx.highlight",
            "pymdownx.superfences",
            "pymdownx.tasklist",
            "pymdownx.tilde",
        ],
        extension_configs={
            "markdown.extensions.toc": {
                "slugify": github_like_slugify,
                "separator": "-",
            },
            "pymdownx.highlight": {
                "guess_lang": False,
            },
            "pymdownx.tasklist": {
                "custom_checkbox": True,
            },
        },
        output_format="html5",
    )
    return md.convert(source)


def github_like_slugify(value: str, separator: str) -> str:
    value = value.strip().lower()
    value = re.sub(r"\s", separator, value)
    value = re.sub(r"[^\w\-]", "", value, flags=re.UNICODE)
    return value.strip(separator)


def extract_title(soup: BeautifulSoup, fallback: str) -> str:
    h1 = soup.find("h1")
    if h1:
        title = h1.get_text(" ", strip=True)
        if title:
            return title
    return fallback


def language_for(source_doc: PurePosixPath) -> str:
    return "en" if source_doc.as_posix() == "README.md" else "ja"


def resolve_doc_link_target(
    source_doc: PurePosixPath,
    raw_path: str,
    doc_map: dict[PurePosixPath, PurePosixPath],
) -> PurePosixPath:
    relative_candidate = normalize_doc_path(source_doc.parent / PurePosixPath(raw_path))
    if relative_candidate in doc_map:
        return relative_candidate

    root_candidate = normalize_doc_path(raw_path)
    if root_candidate in doc_map:
        return root_candidate

    return relative_candidate


def rewrite_links(
    soup: BeautifulSoup,
    source_doc: PurePosixPath,
    output_doc: PurePosixPath,
    doc_map: dict[PurePosixPath, PurePosixPath],
) -> None:
    for anchor in soup.find_all("a", href=True):
        href = anchor["href"]
        if not href or href.startswith("#") or is_external_href(href):
            continue

        parsed = urlsplit(href)
        if not parsed.path:
            continue

        raw_path = unquote(parsed.path).replace("\\", "/")
        if raw_path.startswith("/"):
            continue

        target_source = resolve_doc_link_target(source_doc, raw_path, doc_map)
        if target_source not in doc_map:
            continue

        target_output = doc_map[target_source]
        new_path = relative_url(output_doc, target_output)
        anchor["href"] = urlunsplit(("", "", new_path, parsed.query, parsed.fragment))


def wrap_tables(soup: BeautifulSoup) -> None:
    for table in soup.find_all("table"):
        wrapper = soup.new_tag("div")
        wrapper["class"] = "table-scroll"
        table.wrap(wrapper)


def build_nav(
    current_output: PurePosixPath,
    titles_by_output: dict[PurePosixPath, str],
) -> str:
    links: list[str] = []
    for output_path, title in titles_by_output.items():
        href = html.escape(relative_url(current_output, output_path), quote=True)
        label = html.escape(title)
        current = ' aria-current="page"' if output_path == current_output else ""
        links.append(f'<a href="{href}"{current}>{label}</a>')
    return "\n".join(links)


def wrap_html(
    body_html: str,
    title: str,
    lang: str,
    current_output: PurePosixPath,
    titles_by_output: dict[PurePosixPath, str],
) -> str:
    escaped_title = html.escape(title)
    nav = build_nav(current_output, titles_by_output)
    return f"""<!doctype html>
<html lang="{html.escape(lang, quote=True)}">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{escaped_title}</title>
  <style>
{CSS}
  </style>
</head>
<body>
  <div class="page-shell">
    <nav class="doc-nav" aria-label="Documents">
      <p class="doc-nav-title">Documents</p>
{nav}
    </nav>
    <main class="doc-content">
{body_html}
    </main>
  </div>
</body>
</html>
"""


def collect_ids(html_path: Path) -> set[str]:
    soup = BeautifulSoup(html_path.read_text(encoding="utf-8"), "html.parser")
    return {tag["id"] for tag in soup.find_all(id=True)}


def validate_links(output_root: Path, generated_outputs: list[PurePosixPath]) -> list[str]:
    errors: list[str] = []
    ids_by_file = {
        output_path: collect_ids(output_root / Path(to_posix(output_path)))
        for output_path in generated_outputs
    }

    for output_path in generated_outputs:
        html_file = output_root / Path(to_posix(output_path))
        soup = BeautifulSoup(html_file.read_text(encoding="utf-8"), "html.parser")
        for anchor in soup.find_all("a", href=True):
            href = anchor["href"]
            if not href or is_external_href(href):
                continue
            parsed = urlsplit(href)
            if parsed.path:
                target = normalize_doc_path(output_path.parent / PurePosixPath(unquote(parsed.path).replace("\\", "/")))
            else:
                target = output_path

            target_file = output_root / Path(to_posix(target))
            if target.suffix.lower() == ".md":
                errors.append(f"{output_path}: raw Markdown link was not converted {href}")
                continue
            if target.suffix.lower() not in {".html", ".md"}:
                continue
            if not target_file.exists():
                errors.append(f"{output_path}: missing link target {href}")
                continue

            if parsed.fragment and target.suffix.lower() == ".html":
                fragment = unquote(parsed.fragment)
                if fragment not in ids_by_file.get(target, set()):
                    errors.append(f"{output_path}: missing anchor {href}")

        for image in soup.find_all("img", src=True):
            src = image["src"]
            if not src or is_external_href(src):
                continue
            parsed = urlsplit(src)
            if not parsed.path:
                continue

            target = normalize_doc_path(output_path.parent / PurePosixPath(unquote(parsed.path).replace("\\", "/")))
            target_file = output_root / Path(to_posix(target))
            if not target_file.exists():
                errors.append(f"{output_path}: missing image target {src}")

    return errors


def copy_docs_tree(source_root: Path, output_root: Path) -> None:
    source_docs = source_root / "docs"
    target_docs = output_root / "docs"
    if not source_docs.exists():
        raise FileNotFoundError(f"docs directory was not found: {source_docs}")
    if source_docs.resolve() == target_docs.resolve():
        return
    shutil.copytree(source_docs, target_docs, dirs_exist_ok=True)


def build_html_docs(
    source_root: Path,
    output_root: Path,
    documents: tuple[str, ...],
    copy_docs: bool,
    skip_link_check: bool,
) -> list[PurePosixPath]:
    source_root = source_root.resolve()
    output_root = output_root.resolve()
    output_root.mkdir(parents=True, exist_ok=True)

    if copy_docs:
        copy_docs_tree(source_root, output_root)

    source_docs = [normalize_doc_path(doc) for doc in documents]
    doc_map = {source_doc: html_path_for(source_doc) for source_doc in source_docs}

    rendered: dict[PurePosixPath, BeautifulSoup] = {}
    titles_by_output: dict[PurePosixPath, str] = {}

    for source_doc in source_docs:
        source_file = source_root / Path(to_posix(source_doc))
        if not source_file.exists():
            raise FileNotFoundError(f"document was not found: {source_file}")

        body = render_markdown(source_file.read_text(encoding="utf-8"))
        soup = BeautifulSoup(body, "html.parser")
        output_doc = doc_map[source_doc]
        rewrite_links(soup, source_doc, output_doc, doc_map)
        wrap_tables(soup)
        rendered[source_doc] = soup
        titles_by_output[output_doc] = NAV_LABELS.get(source_doc.as_posix(), extract_title(soup, source_doc.name))

    generated_outputs: list[PurePosixPath] = []
    for source_doc in source_docs:
        output_doc = doc_map[source_doc]
        body_html = str(rendered[source_doc])
        title = titles_by_output[output_doc]
        page_html = wrap_html(body_html, title, language_for(source_doc), output_doc, titles_by_output)

        output_file = output_root / Path(to_posix(output_doc))
        output_file.parent.mkdir(parents=True, exist_ok=True)
        output_file.write_text(page_html, encoding="utf-8", newline="\n")
        generated_outputs.append(output_doc)

    if not skip_link_check:
        errors = validate_links(output_root, generated_outputs)
        if errors:
            joined = "\n".join("  - " + error for error in errors)
            raise RuntimeError("Generated HTML link validation failed:\n" + joined)

    return generated_outputs


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Build release HTML documents from Markdown.")
    parser.add_argument("--source-root", default=".", help="Repository/source root. Defaults to the current directory.")
    parser.add_argument("--output-root", required=True, help="Output root for generated HTML files.")
    parser.add_argument(
        "--document",
        action="append",
        dest="documents",
        help="Markdown document to convert, relative to source root. Can be passed multiple times.",
    )
    parser.add_argument("--copy-docs", action="store_true", help="Copy the docs directory into the output root first.")
    parser.add_argument("--skip-link-check", action="store_true", help="Skip validation of generated local links.")
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    documents = tuple(args.documents) if args.documents else DEFAULT_DOCUMENTS
    try:
        outputs = build_html_docs(
            source_root=Path(args.source_root),
            output_root=Path(args.output_root),
            documents=documents,
            copy_docs=args.copy_docs,
            skip_link_check=args.skip_link_check,
        )
    except Exception as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    for output in outputs:
        print(output.as_posix())
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
