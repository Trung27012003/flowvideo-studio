"""Real-media integration checks. No AI credentials or paid requests required."""
import json, os, subprocess, tempfile, time, urllib.request, urllib.error, uuid
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
BASE='http://127.0.0.1:5097/api'
def request(path, payload=None):
    data=json.dumps(payload).encode() if payload is not None else None
    req=urllib.request.Request(BASE+path,data=data,headers={'Content-Type':'application/json'} if data else {})
    with urllib.request.urlopen(req,timeout=20) as response:return json.load(response)
def upload(project,path):
    boundary='----'+uuid.uuid4().hex
    body=(f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{path.name}"\r\nContent-Type: application/octet-stream\r\n\r\n').encode()+path.read_bytes()+f'\r\n--{boundary}--\r\n'.encode()
    req=urllib.request.Request(BASE+f'/projects/{project}/assets',data=body,headers={'Content-Type':f'multipart/form-data; boundary={boundary}'})
    with urllib.request.urlopen(req) as response:return json.load(response)
def ffmpeg(*args):subprocess.run(['ffmpeg','-y','-hide_banner','-loglevel','error',*args],check=True)
def start(data,log):
    env=dict(os.environ,STUDIO_DATA=str(data),OPENAI_API_KEY='')
    proc=subprocess.Popen(['dotnet','run','--no-build','--configuration','Release','--project',str(ROOT/'src/Studio.Api'),'--urls','http://127.0.0.1:5097'],cwd=ROOT,env=env,stdout=log,stderr=log)
    for _ in range(100):
        if proc.poll() is not None:raise AssertionError('Backend exited; inspect smoke log')
        try:request('/health');return proc
        except (OSError,urllib.error.URLError):time.sleep(.2)
    proc.terminate();raise AssertionError('Backend startup timeout')
def wait_job(project,job):
    for _ in range(180):
        state=next(j for j in request(f'/projects/{project}/jobs') if j['id']==job['id'])
        if state['status']=='Failed':raise AssertionError(state)
        if state['status']=='Succeeded':return state
        time.sleep(.5)
    raise AssertionError('Render timeout')
def probe(path):return json.loads(subprocess.check_output(['ffprobe','-v','error','-show_format','-show_streams','-of','json',str(path)]))
with tempfile.TemporaryDirectory(prefix='flowvideo-smoke-') as folder:
    folder=Path(folder)
    with (folder/'backend.log').open('w+') as log:
        proc=start(folder/'data',log)
        try:
            p=request('/projects',{'name':'Smoke test'})['id']
            ffmpeg('-f','lavfi','-i','color=c=teal:s=320x240:d=1','-c:v','libx264','-pix_fmt','yuv420p',str(folder/'clip.mp4'))
            ffmpeg('-f','lavfi','-i','sine=frequency=440:duration=1','-c:a','pcm_s16le',str(folder/'voice.wav'))
            ffmpeg('-f','lavfi','-i','color=c=orange:s=320x240','-frames:v','1',str(folder/'image.png'))
            (folder/'captions.srt').write_text('1\n00:00:00,000 --> 00:00:01,000\nXin chào FlowVideo\n',encoding='utf-8')
            clip=upload(p,folder/'clip.mp4')['id'];voice=upload(p,folder/'voice.wav')['id'];sub=upload(p,folder/'captions.srt')['id'];image=upload(p,folder/'image.png')['id']
            for mode,ids,duration,audio in [('concat',[clip,clip],2,False),('loop',[clip],3,True),('concat',[image],2,False)]:
                body={'assetIds':ids,'mode':mode,'duration':duration,'ratio':'16:9'}
                if audio:body.update(voiceId=voice,musicId=voice,subtitleId=sub)
                job=request(f'/projects/{p}/renders',body);wait_job(p,job)
                output=folder/(job['id']+'.mp4')
                urllib.request.urlretrieve(BASE+f'/jobs/{job["id"]}/download',output)
                info=probe(output);actual=float(info['format']['duration'])
                assert abs(actual-duration)<.2,(mode,actual,duration)
                video=next(s for s in info['streams'] if s['codec_type']=='video')
                assert (video['width'],video['height'])==(1280,720)
                assert any(s['codec_type']=='audio' for s in info['streams'])==audio
                print(f'PASS {mode}: {len(ids)} source(s), {actual}s, audio={audio}')
            for body in [{'assetIds':[voice]},{'assetIds':[clip],'duration':0},{'assetIds':[clip,clip],'mode':'loop'}]:
                try:request(f'/projects/{p}/renders',body);raise AssertionError('Invalid request accepted')
                except urllib.error.HTTPError as e:assert e.code==400
            other=request('/projects',{'name':'Other'})['id']
            try:request(f'/projects/{other}/renders',{'assetIds':[clip]});raise AssertionError('Cross-project reference accepted')
            except urllib.error.HTTPError as e:assert e.code==400
            try:request('/ai/script',{'idea':'test'});raise AssertionError('Missing AI key accepted')
            except urllib.error.HTTPError as e:assert e.code==503
            # Invalid source must fail and remain retryable without blocking the queue.
            (folder/'broken.mp4').write_text('invalid media')
            broken=upload(p,folder/'broken.mp4')['id']
            bad=request(f'/projects/{p}/renders',{'assetIds':[broken],'duration':1})
            for _ in range(60):
                state=next(j for j in request(f'/projects/{p}/jobs') if j['id']==bad['id'])
                if state['status']=='Failed':break
                time.sleep(.2)
            assert state['status']=='Failed'
            retry=urllib.request.Request(BASE+f'/jobs/{bad["id"]}/retry',data=b'',method='POST')
            with urllib.request.urlopen(retry) as res:assert json.load(res)['status']=='Queued'
            proc.terminate();proc.wait(timeout=15);proc=start(folder/'data',log)
            assert len(request('/projects'))==2
            assert len(request(f'/projects/{p}/jobs'))==4
            print('PASS validation, project isolation, missing AI key, failure/retry and restart persistence')
        except Exception:
            log.flush();log.seek(0);print(log.read()[-10000:]);raise
        finally:
            proc.terminate();proc.wait(timeout=15)
print('All smoke tests passed.')
