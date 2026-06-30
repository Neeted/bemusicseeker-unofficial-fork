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
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from urllib.parse import unquote, urlsplit, urlunsplit

import markdown
from bs4 import BeautifulSoup


@dataclass(frozen=True)
class Document:
    key: str
    lang: str
    source: str
    nav_label: str


DOCUMENTS = (
    Document("readme", "ja", "README.ja.md", "README"),
    Document("readme", "en", "README.md", "README"),
    Document("manual", "ja", "docs/manual.ja.md", "ユーザーマニュアル"),
    Document("manual", "en", "docs/manual.md", "User Manual"),
    Document("search", "ja", "docs/keyword-search-syntax-guide.ja.md", "キーワード検索構文ガイド"),
    Document("search", "en", "docs/keyword-search-syntax-guide.md", "Keyword Search Syntax Guide"),
    Document("logs", "ja", "docs/log-level-info-guide.md", "ログレベル INFO ガイド"),
)

LANGUAGE_LABELS = {
    "ja": "日本語",
    "en": "English",
}

DEFAULT_DOCUMENTS = tuple(document.source for document in DOCUMENTS)

EXTERNAL_SCHEMES = {
    "http",
    "https",
    "mailto",
    "tel",
    "ftp",
    "file",
}

SITE_DESCRIPTIONS = {
    "ja": (
        "BMSライブラリ管理・譜面導入・プレイリスト管理を支援する BeMusicSeeker の非公式フォークです。"
        "高速化、bmson対応、スタンドアローンモード、LR2連携などを改善しています。"
    ),
    "en": (
        "An unofficial BeMusicSeeker fork for managing BMS libraries, installing charts, and maintaining playlists, "
        "with performance improvements, bmson support, standalone mode, and LR2 integration updates."
    ),
}

TWITTER_DESCRIPTIONS = {
    "ja": "BMSライブラリ管理・譜面導入・プレイリスト管理を支援する BeMusicSeeker の非公式フォークです。",
    "en": "An unofficial BeMusicSeeker fork for BMS library management, chart installation, and playlist maintenance.",
}

SITE_OGP_IMAGE = "img/ogp-card.ja.png"
SITE_OGP_IMAGE_WIDTH = 1200
SITE_OGP_IMAGE_HEIGHT = 630
SITE_OGP_IMAGE_ALT = (
    "BeMusicSeeker Unofficial Fork - BMSライブラリ管理を、もっと軽快に。"
    "Releases から最新版を入手。"
)

ALERT_TYPES = {
    "NOTE": ("note", "Note"),
    "TIP": ("tip", "Tip"),
    "IMPORTANT": ("important", "Important"),
    "WARNING": ("warning", "Warning"),
    "CAUTION": ("caution", "Caution"),
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
  --alert-note: #0969da;
  --alert-tip: #1a7f37;
  --alert-important: #8250df;
  --alert-warning: #9a6700;
  --alert-caution: #cf222e;
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

.sidebar-section + .sidebar-section {
  margin-top: 18px;
  padding-top: 16px;
  border-top: 1px solid var(--border-soft);
}

.sidebar-title {
  margin: 0 0 8px;
  color: var(--muted);
  font-size: 12px;
  font-weight: 700;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.doc-nav a,
.toc-tree a {
  display: block;
  padding: 5px 8px;
  border-radius: 6px;
  color: var(--text);
  line-height: 1.38;
  text-decoration: none;
}

.doc-nav a:hover,
.toc-tree a:hover {
  background: #eef5f4;
  color: var(--accent-strong);
}

.doc-nav a[aria-current="page"],
.language-menu a[aria-current="true"],
.toc-tree a[aria-current="true"] {
  background: #dff0ed;
  color: var(--accent-strong);
  font-weight: 700;
}

.language-menu {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.language-menu a {
  border: 1px solid var(--border);
}

.language-menu a[aria-current="true"] {
  border-color: #b7ded8;
}

.document-list a + a {
  margin-top: 4px;
}

.toc-tree {
  max-height: calc(100vh - 280px);
  overflow: auto;
  padding-right: 4px;
}

.toc-tree ul {
  margin: 0;
  padding-left: 0;
  list-style: none;
}

.toc-tree li {
  margin: 1px 0;
}

.toc-tree ul ul {
  margin-left: 12px;
  padding-left: 10px;
  border-left: 1px solid var(--border-soft);
}

.toc-tree details {
  margin: 1px 0;
}

.toc-tree summary {
  display: flex;
  align-items: center;
  gap: 4px;
  min-height: 28px;
  padding: 2px 4px;
  border-radius: 6px;
  color: var(--text);
  cursor: pointer;
}

.toc-tree summary:hover {
  background: #eef5f4;
}

.toc-tree summary::marker {
  color: var(--muted);
}

.toc-tree summary a {
  flex: 1;
  min-width: 0;
  padding: 3px 4px;
}

.toc-tree .toc-level-3 a,
.toc-tree .toc-level-4 a {
  color: #2f6883;
  font-size: 14px;
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

.markdown-alert {
  padding: 12px 16px;
  border-left: 4px solid var(--alert-color);
  background: #fff;
  color: var(--text);
}

.markdown-alert-title {
  margin: 0 0 8px;
  color: var(--alert-color);
  font-weight: 700;
}

.markdown-alert-note {
  --alert-color: var(--alert-note);
}

.markdown-alert-tip {
  --alert-color: var(--alert-tip);
}

.markdown-alert-important {
  --alert-color: var(--alert-important);
}

.markdown-alert-warning {
  --alert-color: var(--alert-warning);
}

.markdown-alert-caution {
  --alert-color: var(--alert-caution);
}

.markdown-alert > :last-child {
  margin-bottom: 0;
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
    padding: 12px;
  }

  .sidebar-section + .sidebar-section {
    margin-top: 12px;
    padding-top: 12px;
  }

  .doc-nav a,
  .toc-tree a {
    padding: 4px 6px;
  }

  .language-menu a {
    flex: 1 1 calc(50% - 3px);
    text-align: center;
  }

  .document-list {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
    gap: 4px;
  }

  .document-list a + a {
    margin-top: 0;
  }

  .doc-content {
    padding: 28px 22px 36px;
  }

  .toc-tree {
    max-height: 220px;
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


def site_html_path_for(markdown_path: PurePosixPath) -> PurePosixPath:
    if markdown_path.name == "README.md":
        return PurePosixPath("index.html")
    if markdown_path.name == "README.ja.md":
        return PurePosixPath("index.ja.html")
    if markdown_path.parts and markdown_path.parts[0] == "docs":
        return html_path_for(PurePosixPath(*markdown_path.parts[1:]))
    return html_path_for(markdown_path)


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


def language_for_source(source_doc: PurePosixPath) -> str:
    return "en" if not source_doc.name.endswith(".ja.md") else "ja"


def build_document_definitions(source_docs: list[PurePosixPath]) -> dict[PurePosixPath, Document]:
    known = {normalize_doc_path(document.source): document for document in DOCUMENTS}
    definitions: dict[PurePosixPath, Document] = {}
    for source_doc in source_docs:
        definitions[source_doc] = known.get(
            source_doc,
            Document(source_doc.as_posix(), language_for_source(source_doc), source_doc.as_posix(), source_doc.name),
        )
    return definitions


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


def output_resource_path(source_resource: PurePosixPath, site_mode: bool) -> PurePosixPath:
    if site_mode and source_resource.parts and source_resource.parts[0] == "docs":
        return PurePosixPath(*source_resource.parts[1:])
    return source_resource


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


def rewrite_images(
    soup: BeautifulSoup,
    source_doc: PurePosixPath,
    output_doc: PurePosixPath,
    site_mode: bool,
) -> None:
    for image in soup.find_all("img", src=True):
        src = image["src"]
        if not src or is_external_href(src):
            continue

        parsed = urlsplit(src)
        if not parsed.path:
            continue

        raw_path = unquote(parsed.path).replace("\\", "/")
        if raw_path.startswith("/"):
            continue

        source_resource = normalize_doc_path(source_doc.parent / PurePosixPath(raw_path))
        output_resource = output_resource_path(source_resource, site_mode)
        new_path = relative_url(output_doc, output_resource)
        image["src"] = urlunsplit(("", "", new_path, parsed.query, parsed.fragment))


def wrap_tables(soup: BeautifulSoup) -> None:
    for table in soup.find_all("table"):
        wrapper = soup.new_tag("div")
        wrapper["class"] = "table-scroll"
        table.wrap(wrapper)


def remove_language_badges(soup: BeautifulSoup) -> None:
    for image in list(soup.find_all("img")):
        if "img.shields.io/badge/lang-" not in (image.get("src") or ""):
            continue
        parent = image.parent
        if parent and parent.name == "a":
            parent.decompose()
        else:
            image.decompose()
    for paragraph in list(soup.find_all("p")):
        if paragraph.get_text(strip=True):
            continue
        if paragraph.find(["img", "picture", "video", "iframe", "object", "embed"]):
            continue
        if paragraph.find(True):
            continue
        paragraph.decompose()


def convert_alert_blocks(soup: BeautifulSoup) -> None:
    marker_pattern = re.compile(r"^\s*\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*(?:\r?\n)?", re.IGNORECASE)
    for blockquote in soup.find_all("blockquote"):
        first_child = next(
            (
                child
                for child in blockquote.children
                if getattr(child, "name", None) or str(child).strip()
            ),
            None,
        )
        if first_child is None or getattr(first_child, "name", None) != "p":
            continue

        first_html = first_child.decode_contents()
        match = marker_pattern.match(first_html)
        if not match:
            continue

        alert_kind = match.group(1).upper()
        alert_class, alert_label = ALERT_TYPES[alert_kind]
        remaining_html = marker_pattern.sub("", first_html, count=1)
        first_child.clear()
        remaining_fragment = BeautifulSoup(remaining_html, "html.parser")
        for child in list(remaining_fragment.contents):
            first_child.append(child)
        if not first_child.get_text(strip=True) and not first_child.find(True):
            first_child.decompose()

        title = soup.new_tag("p")
        title["class"] = "markdown-alert-title"
        title.string = alert_label
        blockquote.name = "div"
        blockquote["class"] = ["markdown-alert", "markdown-alert-" + alert_class]
        blockquote.insert(0, title)


def extract_headings(soup: BeautifulSoup) -> list[dict[str, str | int]]:
    headings: list[dict[str, str | int]] = []
    for heading in soup.find_all(["h1", "h2", "h3", "h4"]):
        heading_id = heading.get("id")
        text = heading.get_text(" ", strip=True)
        if not heading_id or not text:
            continue
        if text in {"目次", "Table of Contents"}:
            continue
        headings.append(
            {
                "level": int(heading.name[1]),
                "id": str(heading_id),
                "text": text,
            }
        )
    return headings


def build_toc_tree(headings: list[dict[str, str | int]]) -> list[dict[str, object]]:
    root: list[dict[str, object]] = []
    stack: list[tuple[int, list[dict[str, object]]]] = [(0, root)]
    for heading in headings:
        level = int(heading["level"])
        node: dict[str, object] = {
            "level": level,
            "id": heading["id"],
            "text": heading["text"],
            "children": [],
        }
        while stack and stack[-1][0] >= level:
            stack.pop()
        stack[-1][1].append(node)
        stack.append((level, node["children"]))  # type: ignore[arg-type]
    return root


def render_toc_nodes(nodes: list[dict[str, object]]) -> str:
    if not nodes:
        return ""
    items: list[str] = []
    for node in nodes:
        level = int(node["level"])
        href = "#" + html.escape(str(node["id"]), quote=True)
        label = html.escape(str(node["text"]))
        children = node["children"]  # type: ignore[assignment]
        child_html = render_toc_nodes(children) if children else ""
        if child_html:
            items.append(
                f'<li class="toc-level-{level}"><details open>'
                f'<summary><a href="{href}">{label}</a></summary>{child_html}</details></li>'
            )
        else:
            items.append(f'<li class="toc-level-{level}"><a href="{href}">{label}</a></li>')
    return "<ul>\n" + "\n".join(items) + "\n</ul>"


def build_language_menu(
    current_doc: Document,
    current_output: PurePosixPath,
    doc_map: dict[PurePosixPath, PurePosixPath],
    doc_defs_by_source: dict[PurePosixPath, Document],
) -> str:
    alternates = [
        (source_doc, document)
        for source_doc, document in doc_defs_by_source.items()
        if document.key == current_doc.key
    ]
    alternates.sort(key=lambda item: list(LANGUAGE_LABELS).index(item[1].lang) if item[1].lang in LANGUAGE_LABELS else 99)
    links: list[str] = []
    for source_doc, document in alternates:
        output_doc = doc_map[source_doc]
        href = html.escape(relative_url(current_output, output_doc), quote=True)
        label = html.escape(LANGUAGE_LABELS.get(document.lang, document.lang))
        current = ' aria-current="true"' if document.lang == current_doc.lang else ""
        links.append(f'<a href="{href}"{current}>{label}</a>')
    return "\n".join(links)


def build_document_links(
    current_doc: Document,
    current_output: PurePosixPath,
    doc_map: dict[PurePosixPath, PurePosixPath],
    doc_defs_by_source: dict[PurePosixPath, Document],
    nav_labels_by_output: dict[PurePosixPath, str],
) -> str:
    links: list[str] = []
    linked_sources: set[PurePosixPath] = set()
    for document in DOCUMENTS:
        if document.lang != current_doc.lang:
            continue
        source_doc = normalize_doc_path(document.source)
        if source_doc not in doc_map:
            continue
        output_path = doc_map[source_doc]
        title = nav_labels_by_output[output_path]
        href = html.escape(relative_url(current_output, output_path), quote=True)
        label = html.escape(title)
        current = ' aria-current="page"' if output_path == current_output else ""
        links.append(f'<a href="{href}"{current}>{label}</a>')
        linked_sources.add(source_doc)
    for source_doc, document in doc_defs_by_source.items():
        if source_doc in linked_sources or document.lang != current_doc.lang:
            continue
        output_path = doc_map[source_doc]
        title = nav_labels_by_output[output_path]
        href = html.escape(relative_url(current_output, output_path), quote=True)
        label = html.escape(title)
        current = ' aria-current="page"' if output_path == current_output else ""
        links.append(f'<a href="{href}"{current}>{label}</a>')
    return "\n".join(links)


def build_sidebar(
    current_doc: Document,
    current_output: PurePosixPath,
    doc_map: dict[PurePosixPath, PurePosixPath],
    doc_defs_by_source: dict[PurePosixPath, Document],
    nav_labels_by_output: dict[PurePosixPath, str],
    headings: list[dict[str, str | int]],
) -> str:
    language_menu = build_language_menu(current_doc, current_output, doc_map, doc_defs_by_source)
    document_links = build_document_links(current_doc, current_output, doc_map, doc_defs_by_source, nav_labels_by_output)
    toc = render_toc_nodes(build_toc_tree(headings))
    toc_section = ""
    if toc:
        toc_section = f"""
      <section class="sidebar-section">
        <p class="sidebar-title">On This Page</p>
        <div class="toc-tree">
{toc}
        </div>
      </section>"""
    return f"""
      <section class="sidebar-section">
        <p class="sidebar-title">Language</p>
        <div class="language-menu">
{language_menu}
        </div>
      </section>
      <section class="sidebar-section">
        <p class="sidebar-title">Documents</p>
        <div class="document-list">
{document_links}
        </div>
      </section>{toc_section}
"""


def join_site_url(site_url: str, output_doc: PurePosixPath) -> str:
    return f"{site_url.rstrip('/')}/{output_doc.as_posix()}"


def build_site_meta(title: str, lang: str, output_doc: PurePosixPath, site_url: str | None) -> str:
    if not site_url:
        return ""

    page_url = join_site_url(site_url, output_doc)
    image_url = f"{site_url.rstrip('/')}/{SITE_OGP_IMAGE}"
    description = SITE_DESCRIPTIONS.get(lang, SITE_DESCRIPTIONS["en"])
    twitter_description = TWITTER_DESCRIPTIONS.get(lang, TWITTER_DESCRIPTIONS["en"])
    escaped_title = html.escape(title, quote=True)
    escaped_description = html.escape(description, quote=True)
    escaped_twitter_description = html.escape(twitter_description, quote=True)
    escaped_page_url = html.escape(page_url, quote=True)
    escaped_image_url = html.escape(image_url, quote=True)
    escaped_image_alt = html.escape(SITE_OGP_IMAGE_ALT, quote=True)

    return f"""
  <meta name="description" content="{escaped_description}">
  <meta property="og:type" content="website">
  <meta property="og:site_name" content="BeMusicSeeker Unofficial Fork">
  <meta property="og:title" content="{escaped_title}">
  <meta property="og:description" content="{escaped_description}">
  <meta property="og:image" content="{escaped_image_url}">
  <meta property="og:image:width" content="{SITE_OGP_IMAGE_WIDTH}">
  <meta property="og:image:height" content="{SITE_OGP_IMAGE_HEIGHT}">
  <meta property="og:image:alt" content="{escaped_image_alt}">
  <meta property="og:url" content="{escaped_page_url}">
  <meta name="twitter:card" content="summary_large_image">
  <meta name="twitter:title" content="{escaped_title}">
  <meta name="twitter:description" content="{escaped_twitter_description}">
  <meta name="twitter:image" content="{escaped_image_url}">
  <meta name="twitter:image:alt" content="{escaped_image_alt}">"""


def wrap_html(
    body_html: str,
    title: str,
    lang: str,
    sidebar_html: str,
    output_doc: PurePosixPath,
    site_url: str | None,
) -> str:
    escaped_title = html.escape(title)
    site_meta = build_site_meta(title, lang, output_doc, site_url)
    return f"""<!doctype html>
<html lang="{html.escape(lang, quote=True)}">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
{site_meta}
  <title>{escaped_title}</title>
  <style>
{CSS}
  </style>
</head>
<body>
  <div class="page-shell">
    <nav class="doc-nav" aria-label="Documents">
{sidebar_html}
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
    if target_docs.exists():
        shutil.rmtree(target_docs)
    shutil.copytree(source_docs, target_docs, dirs_exist_ok=True)


def build_html_docs(
    source_root: Path,
    output_root: Path,
    documents: tuple[str, ...],
    copy_docs: bool,
    skip_link_check: bool,
    site_mode: bool,
    site_url: str | None,
) -> list[PurePosixPath]:
    source_root = source_root.resolve()
    output_root = output_root.resolve()
    output_root.mkdir(parents=True, exist_ok=True)

    if copy_docs:
        copy_docs_tree(source_root, output_root)

    source_docs = [normalize_doc_path(doc) for doc in documents]
    path_builder = site_html_path_for if site_mode else html_path_for
    doc_map = {source_doc: path_builder(source_doc) for source_doc in source_docs}
    doc_defs_by_source = build_document_definitions(source_docs)

    rendered: dict[PurePosixPath, BeautifulSoup] = {}
    titles_by_output: dict[PurePosixPath, str] = {}
    nav_labels_by_output: dict[PurePosixPath, str] = {}
    headings_by_output: dict[PurePosixPath, list[dict[str, str | int]]] = {}

    for source_doc in source_docs:
        source_file = source_root / Path(to_posix(source_doc))
        if not source_file.exists():
            raise FileNotFoundError(f"document was not found: {source_file}")

        body = render_markdown(source_file.read_text(encoding="utf-8"))
        soup = BeautifulSoup(body, "html.parser")
        output_doc = doc_map[source_doc]
        rewrite_links(soup, source_doc, output_doc, doc_map)
        rewrite_images(soup, source_doc, output_doc, site_mode)
        remove_language_badges(soup)
        convert_alert_blocks(soup)
        wrap_tables(soup)
        rendered[source_doc] = soup
        titles_by_output[output_doc] = extract_title(soup, source_doc.name)
        nav_labels_by_output[output_doc] = doc_defs_by_source[source_doc].nav_label
        headings_by_output[output_doc] = extract_headings(soup)

    generated_outputs: list[PurePosixPath] = []
    for source_doc in source_docs:
        output_doc = doc_map[source_doc]
        body_html = str(rendered[source_doc])
        title = titles_by_output[output_doc]
        current_doc = doc_defs_by_source[source_doc]
        sidebar = build_sidebar(
            current_doc,
            output_doc,
            doc_map,
            doc_defs_by_source,
            nav_labels_by_output,
            headings_by_output[output_doc],
        )
        page_html = wrap_html(body_html, title, current_doc.lang, sidebar, output_doc, site_url)

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
    parser.add_argument(
        "--site",
        action="store_true",
        help="Generate GitHub Pages style paths: README files become index*.html and docs/*.md are written at the output root.",
    )
    parser.add_argument(
        "--site-url",
        help="Absolute public site root used for OGP/Twitter card metadata. Intended for use with --site.",
    )
    parser.add_argument("--skip-link-check", action="store_true", help="Skip validation of generated local links.")
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    if args.site and args.copy_docs:
        print("error: --site cannot be combined with --copy-docs", file=sys.stderr)
        return 2
    if args.site_url and not args.site:
        print("error: --site-url requires --site", file=sys.stderr)
        return 2

    documents = tuple(args.documents) if args.documents else DEFAULT_DOCUMENTS
    try:
        outputs = build_html_docs(
            source_root=Path(args.source_root),
            output_root=Path(args.output_root),
            documents=documents,
            copy_docs=args.copy_docs,
            skip_link_check=args.skip_link_check,
            site_mode=args.site,
            site_url=args.site_url.rstrip("/") if args.site_url else None,
        )
    except Exception as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    for output in outputs:
        print(output.as_posix())
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
