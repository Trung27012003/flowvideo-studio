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
def post(path):
    req=urllib.request.Request(BASE+path,data=b'',method='POST')
    with urllib.request.urlopen(req,timeout=20) as response:return json.load(response)
def expect_status(path,code,payload=None,method=None):
    data=json.dumps(payload).encode() if payload is not None else b'' if method=='POST' else None
    req=urllib.request.Request(BASE+path,data=data,method=method,headers={'Content-Type':'application/json'} if payload is not None else {})
    try:urllib.request.urlopen(req,timeout=20);raise AssertionError(f'Expected {code}: {path}')
    except urllib.error.HTTPError as e:assert e.code==code,(e.code,code)
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
            # Verify actual pixels and decoded audio for composition controls.
            import struct, math
            outputs={}
            for label,options in [('contain',{}),('cover',{'fit':'cover'}),('fade',{'fit':'cover','fadeSeconds':.4}),('duck',{'duckMusic':True})]:
                job=request(f'/projects/{p}/renders',{'assetIds':[clip],'mode':'loop','duration':2,'ratio':'16:9','voiceId':voice,'musicId':voice,'musicGain':1,**options})
                wait_job(p,job)
                output=folder/('compose-'+label+'.mp4');urllib.request.urlretrieve(BASE+f'/jobs/{job["id"]}/download',output);outputs[label]=output
                assert abs(float(probe(output)['format']['duration'])-2)<.15
            def pixel_at(file,at,x,y):
                return subprocess.check_output(['ffmpeg','-hide_banner','-loglevel','error','-ss',str(at),'-i',str(file),'-vf',f'crop=2:2:{x}:{y},scale=1:1','-frames:v','1','-f','rawvideo','-pix_fmt','rgb24','pipe:1'])
            assert max(pixel_at(outputs['contain'],.5,2,360))<10
            assert pixel_at(outputs['cover'],.5,2,360)[1]>80
            assert max(pixel_at(outputs['fade'],0,640,360))<10
            assert pixel_at(outputs['fade'],.8,640,360)[1]>80
            assert max(pixel_at(outputs['fade'],1.96,640,360))<30
            def audio_rms(file,start,duration):
                raw=subprocess.check_output(['ffmpeg','-hide_banner','-loglevel','error','-ss',str(start),'-i',str(file),'-t',str(duration),'-map','0:a:0','-ac','1','-ar','16000','-f','f32le','pipe:1'])
                values=struct.unpack('<'+'f'*(len(raw)//4),raw)
                return math.sqrt(sum(v*v for v in values)/len(values))
            assert audio_rms(outputs['duck'],.2,.5)<audio_rms(outputs['contain'],.2,.5)*.85
            assert audio_rms(outputs['fade'],0,.05)<audio_rms(outputs['fade'],.5,.1)*.3
            print('PASS contain/cover pixels, fade in/out pixels and audio, sidechain music ducking, loop duration')
            # Trim must select the requested content, not just shorten the output.
            ffmpeg('-f','lavfi','-i','color=c=red:s=320x240:d=1','-f','lavfi','-i','color=c=blue:s=320x240:d=1','-filter_complex','[0:v][1:v]concat=n=2:v=1:a=0[v]','-map','[v]','-c:v','libx264','-pix_fmt','yuv420p',str(folder/'colors.mp4'))
            colors=upload(p,folder/'colors.mp4')['id']
            job=request(f'/projects/{p}/renders',{'assetIds':[colors],'duration':2,'ratio':'9:16','cuts':[{'assetId':colors,'start':1,'end':2}]})
            wait_job(p,job)
            trimmed=folder/'trimmed.mp4';urllib.request.urlretrieve(BASE+f'/jobs/{job["id"]}/download',trimmed)
            assert abs(float(probe(trimmed)['format']['duration'])-1)<.15
            pixel=subprocess.check_output(['ffmpeg','-hide_banner','-loglevel','error','-i',str(trimmed),'-vf','crop=2:2:(iw-2)/2:(ih-2)/2,scale=1:1','-frames:v','1','-f','rawvideo','-pix_fmt','rgb24','pipe:1'])
            assert pixel[2]>180 and pixel[0]<60,tuple(pixel)
            print('PASS trim start/end: correct blue scene and vertical output')
            for cut in [{'assetId':colors,'start':-1},{'assetId':colors,'start':1,'end':.5},{'assetId':voice,'start':0},None]:
                expect_status(f'/projects/{p}/renders',400,{'assetIds':[colors],'cuts':[cut]})
            # Rendering stops at the duration budget, without processing unused sources.
            (folder/'unused.mp4').write_text('invalid media')
            unused=upload(p,folder/'unused.mp4')['id']
            job=request(f'/projects/{p}/renders',{'assetIds':[clip,unused],'duration':1})
            wait_job(p,job)
            expect_status(f'/jobs/{job["id"]}/cancel',409,method='POST')
            # Hold the worker with a long subtitle encode, cancel a queued job,
            # then cancel the running process and verify the next job can finish.
            blocker=request(f'/projects/{p}/renders',{'assetIds':[clip],'mode':'loop','duration':600,'subtitleId':sub})
            for _ in range(100):
                current=next(j for j in request(f'/projects/{p}/jobs') if j['id']==blocker['id'])
                if current['status']=='Running':break
                time.sleep(.1)
            assert current['status']=='Running'
            queued=request(f'/projects/{p}/renders',{'assetIds':[clip],'duration':1})
            assert post(f'/jobs/{queued["id"]}/cancel')['status']=='Cancelled'
            assert post(f'/jobs/{queued["id"]}/cancel')['status']=='Cancelled'
            # Allow FFmpeg to start before cancellation.
            time.sleep(.3)
            assert post(f'/jobs/{blocker["id"]}/cancel')['status']=='Cancelled'
            follower=request(f'/projects/{p}/renders',{'assetIds':[clip],'duration':1})
            wait_job(p,follower)
            states={j['id']:j['status'] for j in request(f'/projects/{p}/jobs')}
            assert states[queued['id']]==states[blocker['id']]=='Cancelled'
            for cancelled in [queued,blocker]:
                expect_status(f'/jobs/{cancelled["id"]}/download',404)
                assert not (folder/'data'/'exports'/(cancelled['id']+'.mp4')).exists()
                assert not (folder/'data'/'temp'/cancelled['id'].replace('-','')).exists()
            assert all('requestJson' not in j for j in request(f'/projects/{p}/jobs'))
            print('PASS early duration cutoff, queued/running cancellation, process cleanup and queue recovery')
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
            job_count=len(request(f'/projects/{p}/jobs'))
            proc.terminate();proc.wait(timeout=15);proc=start(folder/'data',log)
            assert len(request('/projects'))==2
            assert len(request(f'/projects/{p}/jobs'))==job_count
            print('PASS validation, project isolation, missing AI key, failure/retry and restart persistence')
        except Exception:
            log.flush();log.seek(0);print(log.read()[-10000:]);raise
        finally:
            proc.terminate();proc.wait(timeout=15)
print('All smoke tests passed.')
