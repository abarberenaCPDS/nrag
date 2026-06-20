#!/usr/bin/env python3
"""
Check all markdown files in the repository for broken links.
Validates both internal links (files, anchors) and external links (HTTP/HTTPS).
"""

import os
import re
import sys
import argparse
from pathlib import Path
from urllib.parse import urlparse, unquote
import requests
from typing import List, Tuple, Set
from collections import defaultdict

# Configure requests session with reasonable timeouts and retries
session = requests.Session()
session.headers.update({
    'User-Agent': 'Mozilla/5.0 (compatible; LinkChecker/1.0)'
})

# Cache for external URL checks to avoid duplicate requests
url_cache = {}

# Set to track already processed external URLs across all files
checked_urls = set()


def find_markdown_files(root_dir: Path) -> List[Path]:
    """Find all markdown files in the repository."""
    md_files = []
    for path in root_dir.rglob("*.md"):
        # Skip hidden directories and common exclusions
        if any(part.startswith('.') for part in path.parts):
            continue
        md_files.append(path)
    return sorted(md_files)


def extract_links(content: str, file_path: Path) -> Tuple[List[str], List[str]]:
    """
    Extract internal and external links from markdown content.
    Returns: (internal_links, external_links)
    """
    internal_links = []
    external_links = []

    # Match markdown links: [text](url)
    link_pattern = r'\[([^\]]+)\]\(([^\)]+)\)'

    # Match HTML links: <a href="url">
    html_link_pattern = r'<a\s+[^>]*href=["\']([^"\']+)["\']'

    # Match reference-style links: [text]: url
    ref_link_pattern = r'^\[([^\]]+)\]:\s*(.+)$'

    for match in re.finditer(link_pattern, content):
        url = match.group(2).strip()
        if url.startswith(('http://', 'https://')):
            external_links.append(url)
        elif not url.startswith(('mailto:', 'javascript:', '#')):
            internal_links.append(url)

    for match in re.finditer(html_link_pattern, content):
        url = match.group(1).strip()
        if url.startswith(('http://', 'https://')):
            external_links.append(url)
        elif not url.startswith(('mailto:', 'javascript:', '#')):
            internal_links.append(url)

    for match in re.finditer(ref_link_pattern, content, re.MULTILINE):
        url = match.group(2).strip()
        if url.startswith(('http://', 'https://')):
            external_links.append(url)
        elif not url.startswith(('mailto:', 'javascript:', '#')):
            internal_links.append(url)

    return internal_links, external_links


def check_internal_link(link: str, source_file: Path, root_dir: Path) -> Tuple[bool, str]:
    """
    Check if an internal link is valid.
    Returns: (is_valid, error_message)
    """
    # Remove anchor if present
    link_parts = link.split('#')
    file_link = link_parts[0]
    anchor = link_parts[1] if len(link_parts) > 1 else None

    # Skip empty file links (pure anchors in same file)
    if not file_link:
        return True, ""

    # Decode URL encoding
    file_link = unquote(file_link)

    # Resolve relative path
    if file_link.startswith('/'):
        # Absolute path from repo root
        target_path = root_dir / file_link.lstrip('/')
    else:
        # Relative path from source file
        target_path = (source_file.parent / file_link).resolve()

    # Check if target exists
    if not target_path.exists():
        return False, f"File not found: {file_link} (resolved to {target_path})"

    # If target exists (either file or directory), it's valid
    # TODO: Could validate anchor exists in target file, but that's complex
    # For now, we just validate file/directory existence

    return True, ""


def check_external_link(url: str) -> Tuple[bool, str]:
    """
    Check if an external link is accessible.
    Returns: (is_valid, error_message)
    """
    # Skip already checked URLs
    if url in checked_urls:
        if url in url_cache:
            return url_cache[url]
        return True, ""

    checked_urls.add(url)

    # Check cache
    if url in url_cache:
        return url_cache[url]

    try:
        # Use HEAD request first (faster)
        response = session.head(url, timeout=10, allow_redirects=True)

        # Some servers don't support HEAD, try GET if HEAD fails
        if response.status_code >= 400:
            response = session.get(url, timeout=10, allow_redirects=True, stream=True)
            # Close connection immediately, we don't need the content
            response.close()

        if response.status_code >= 400:
            result = (False, f"HTTP {response.status_code}")
            url_cache[url] = result
            return result

        result = (True, "")
        url_cache[url] = result
        return result

    except requests.exceptions.Timeout:
        result = (False, "Timeout")
        url_cache[url] = result
        return result
    except requests.exceptions.ConnectionError:
        result = (False, "Connection error")
        url_cache[url] = result
        return result
    except requests.exceptions.TooManyRedirects:
        result = (False, "Too many redirects")
        url_cache[url] = result
        return result
    except Exception as e:
        result = (False, f"Error: {str(e)}")
        url_cache[url] = result
        return result


def check_markdown_file(file_path: Path, root_dir: Path, check_external: bool = True) -> List[str]:
    """
    Check all links in a markdown file.
    Returns list of error messages.
    """
    errors = []

    try:
        content = file_path.read_text(encoding='utf-8')
    except Exception as e:
        return [f"Error reading file: {e}"]

    internal_links, external_links = extract_links(content, file_path)

    # Check internal links
    for link in internal_links:
        is_valid, error = check_internal_link(link, file_path, root_dir)
        if not is_valid:
            errors.append(f"Internal link broken: [{link}] - {error}")

    # Check external links
    if check_external:
        for url in external_links:
            # Skip links to docs.nvidia.com
            if url.startswith('https://docs.nvidia.com'):
                continue
            is_valid, error = check_external_link(url)
            if not is_valid:
                errors.append(f"External link broken: [{url}] - {error}")

    return errors


def main():
    parser = argparse.ArgumentParser(
        description='Check markdown files for broken links'
    )
    parser.add_argument(
        '--root',
        type=Path,
        default=Path.cwd(),
        help='Root directory of the repository (default: current directory)'
    )
    parser.add_argument(
        '--no-external',
        action='store_true',
        help='Skip checking external links'
    )
    parser.add_argument(
        '--fail-fast',
        action='store_true',
        help='Exit immediately on first error'
    )

    args = parser.parse_args()
    root_dir = args.root.resolve()
    check_external = not args.no_external

    print(f"Checking markdown links in: {root_dir}")
    print(f"External link checking: {'enabled' if check_external else 'disabled'}")
    print()

    # Find all markdown files
    md_files = find_markdown_files(root_dir)
    print(f"Found {len(md_files)} markdown files")
    print()

    # Track errors by file
    file_errors = defaultdict(list)
    total_errors = 0

    # Check each file
    for i, md_file in enumerate(md_files, 1):
        rel_path = md_file.relative_to(root_dir)
        print(f"[{i}/{len(md_files)}] Checking {rel_path}...", end=' ')

        errors = check_markdown_file(md_file, root_dir, check_external)

        if errors:
            print(f"❌ {len(errors)} error(s)")
            file_errors[rel_path] = errors
            total_errors += len(errors)

            if args.fail_fast:
                print("\nErrors found:")
                for error in errors:
                    print(f"  - {error}")
                sys.exit(1)
        else:
            print("✓")

    print()
    print("=" * 80)

    # Report results
    if file_errors:
        print(f"\n❌ Found {total_errors} broken link(s) in {len(file_errors)} file(s):\n")

        for file_path, errors in sorted(file_errors.items()):
            print(f"\n{file_path}:")
            for error in errors:
                print(f"  - {error}")

        print("\n" + "=" * 80)
        print(f"Summary: {total_errors} broken link(s) in {len(file_errors)} file(s)")
        sys.exit(1)
    else:
        print(f"✅ All links are valid! Checked {len(md_files)} files.")
        sys.exit(0)


if __name__ == "__main__":
    main()

