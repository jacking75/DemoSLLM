"""공개된 약관 HTML의 본문만 저장한다. 로그인이나 약관 동의는 수행하지 않는다."""
import os
from html.parser import HTMLParser
from pathlib import Path

class Article(HTMLParser):
    def __init__(self):
        super().__init__()
        self.depth = 0
        self.output = []
    def handle_starttag(self, tag, attrs):
        if "devsite-article-body" in dict(attrs).get("class", ""):
            self.depth = 1
        elif self.depth and tag not in {"br", "hr", "img", "input", "meta", "link"}:
            self.depth += 1
        if self.depth and tag in {"p", "h1", "h2", "h3", "li", "br"}:
            self.output.append("\n")
    def handle_endtag(self, tag):
        if self.depth and tag not in {"br", "hr", "img", "input", "meta", "link"}:
            self.depth -= 1
    def handle_data(self, data):
        if self.depth:
            self.output.append(data)

root = Path(__file__).resolve().parent.parent
for key, target, url in [
    ("terms", "Gemma-Terms.txt", "https://ai.google.dev/gemma/terms"),
    ("policy", "Gemma-Prohibited-Use-Policy.txt", "https://ai.google.dev/gemma/prohibited_use_policy"),
]:
    source = Path(os.environ["TEMP"]) / f"localmind-gemma-{key}.html"
    parser = Article()
    parser.feed(source.read_text(encoding="utf-8"))
    text = "\n".join(line.strip() for line in "".join(parser.output).splitlines() if line.strip())
    if len(text) < 1000 or (key == "terms" and "Distribution" not in text):
        raise ValueError("공식 본문 추출이 불완전하다")
    (root / "third_party/licenses" / target).write_text(f"Source: {url}\nRetrieved: 2026-10-01 KST\n\n{text}\n", encoding="utf-8")
    print(target, len(text))
