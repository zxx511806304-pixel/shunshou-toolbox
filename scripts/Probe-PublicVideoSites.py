"""Online extractor checks, bounded independently from fixture-only unit tests."""
import concurrent.futures, json, pathlib, subprocess, time
root=pathlib.Path(__file__).resolve().parents[1]
engine=root/'dist/ShunshouToolbox-1.0.2-win-x64/app/tools/video-download'
out=root/'artifacts/web-tools-research'; out.mkdir(parents=True,exist_ok=True)
samples={
 'bilibili-1':'https://www.bilibili.com/video/BV13x41117TL',
 'bilibili-2':'https://www.bilibili.com/video/BV1bK411W797',
 'douyin-1':'https://www.douyin.com/video/6961737553342991651',
 'douyin-2':'https://www.douyin.com/video/6982497745948921092',
 'xiaohongshu-1':'https://www.xiaohongshu.com/explore/6411cf99000000001300b6d9',
 'xiaohongshu-2':'https://www.xiaohongshu.com/discovery/item/674051740000000007027a15?xsec_token=CBgeL8Dxd1ZWBhwqRd568gAZ_iwG-9JIf9tnApNmteU2E=',
}
def probe(item):
 name,url=item; start=time.monotonic()
 args=[str(engine/'python.exe'),'-I',str(engine/'yt-dlp'),'--ignore-config','--no-plugin-dirs','--no-cache-dir','--no-remote-components','--no-playlist','--socket-timeout','15','--retries','0','--dump-single-json','--skip-download','--',url]
 result={'site':name,'url':url}
 try:
  p=subprocess.run(args,capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=65)
  result['exit']=p.returncode
  if p.returncode==0:
   info=json.loads(p.stdout); (out/(name+'.info.json')).write_text(p.stdout,encoding='utf-8')
   result.update(title=info.get('title'),duration=info.get('duration'),formats=[{k:f.get(k) for k in ('format_id','ext','vcodec','acodec','height')} for f in info.get('formats',[])])
  else: result['error']=p.stderr[-2200:]
 except Exception as ex: result['error']=str(ex)
 result['seconds']=round(time.monotonic()-start,2); return result
results=list(concurrent.futures.ThreadPoolExecutor(max_workers=3).map(probe,samples.items()))
(out/'video-live-baseline.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(results,ensure_ascii=False,indent=2))
