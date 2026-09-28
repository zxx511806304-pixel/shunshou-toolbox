"""Bounded, read-only upstream checks; never reads browser profiles or cookies."""
import concurrent.futures, hashlib, json, pathlib, re, urllib.request

root = pathlib.Path(__file__).resolve().parents[1] / 'artifacts' / 'web-tools-research'
root.mkdir(parents=True, exist_ok=True)
sources = {
    'yt-latest.json': 'https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest',
    'tiktok.py': 'https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/yt_dlp/extractor/tiktok.py',
    'xiaohongshu.py': 'https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/yt_dlp/extractor/xiaohongshu.py',
    'bilibili.py': 'https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/yt_dlp/extractor/bilibili.py',
    'kuaishou.py': 'https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/yt_dlp/extractor/kuaishou.py',
    'readability-package.json': 'https://raw.githubusercontent.com/mozilla/readability/main/package.json',
    'readability-release.json': 'https://api.github.com/repos/mozilla/readability/releases/latest',
}
def fetch(item):
    name, url = item
    try:
        req = urllib.request.Request(url, headers={'User-Agent':'ShunshouToolbox-CompatibilityCheck/1.1'})
        with urllib.request.urlopen(req, timeout=25) as response: body = response.read(4_000_000)
        (root / name).write_bytes(body)
        result = {'file':name,'url':url,'bytes':len(body),'sha256':hashlib.sha256(body).hexdigest()}
        if name.endswith('.py'):
            result['examples'] = list(dict.fromkeys(re.findall(r"https://(?:www\.)?(?:douyin\.com|bilibili\.com|xiaohongshu\.com|kuaishou\.com|v\.douyin\.com|xhslink\.com)/[^'\"\s<>]+", body.decode())))[:14]
        else:
            obj=json.loads(body); result.update({k:obj[k] for k in ['tag_name','version','published_at'] if k in obj})
        return result
    except Exception as ex: return {'file':name,'url':url,'error':str(ex)}
results=list(concurrent.futures.ThreadPoolExecutor(max_workers=4).map(fetch,sources.items()))
(root/'upstream.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(results,ensure_ascii=False,indent=2))
