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
    env=dict(os.environ,STUDIO_DATA=str(data),OPENAI_API_KEY='',GEMINI_API_KEY='')
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
            # v0.8 effects: validate decoded pixels, fps, delayed/sped voice and normalized level.
            effects_outputs={}
            for label,effects in [('gray',{'brightness':.1,'contrast':1.2,'saturation':0}),('border',{'background':'#ff00ff','fps':60,'quality':'high'}),('voice',{'voiceDelay':.25,'voiceSpeed':1.5,'normalizeVoice':True,'mono':True})]:
                body={'assetIds':[clip],'mode':'loop','duration':2,'ratio':'16:9','effects':effects}
                if label=='voice':body['voiceId']=voice
                job=request(f'/projects/{p}/renders',body);wait_job(p,job)
                output=folder/('effect-'+label+'.mp4');urllib.request.urlretrieve(BASE+f'/jobs/{job["id"]}/download',output);effects_outputs[label]=output
            gray=pixel_at(effects_outputs['gray'],.5,640,360);assert max(gray)-min(gray)<5 and min(gray)>70,tuple(gray)
            border=pixel_at(effects_outputs['border'],.5,2,360);assert border[0]>220 and border[1]<30 and border[2]>220,tuple(border)
            assert next(s for s in probe(effects_outputs['border'])['streams'] if s['codec_type']=='video')['r_frame_rate']=='60/1'
            voice_stream=next(s for s in probe(effects_outputs['voice'])['streams'] if s['codec_type']=='audio');assert voice_stream['channels']==1
            assert audio_rms(effects_outputs['voice'],.02,.1)<.003
            assert audio_rms(effects_outputs['voice'],.35,.2)>.04
            assert audio_rms(effects_outputs['voice'],1.1,.2)<.003
            ffmpeg('-f','lavfi','-i','color=c=red:s=320x240:d=1','-vf','drawbox=x=160:y=0:w=160:h=120:c=blue:t=fill,drawbox=x=0:y=120:w=160:h=120:c=green:t=fill,drawbox=x=160:y=120:w=160:h=120:c=yellow:t=fill','-c:v','libx264','-pix_fmt','yuv420p',str(folder/'quadrants.mp4'))
            quadrants=upload(p,folder/'quadrants.mp4')['id']
            job=request(f'/projects/{p}/renders',{'assetIds':[quadrants],'duration':1,'ratio':'16:9','fit':'cover','effects':{'rotation':90,'flipHorizontal':True,'flipVertical':True,'sharpen':.5,'vignette':True,'quality':'compact','fps':24}});wait_job(p,job)
            oriented=folder/'oriented.mp4';urllib.request.urlretrieve(BASE+f'/jobs/{job["id"]}/download',oriented)
            corner=pixel_at(oriented,.3,320,180);assert corner[2]>150 and corner[0]<40,tuple(corner)
            assert next(s for s in probe(oriented)['streams'] if s['codec_type']=='video')['r_frame_rate']=='24/1'
            print('PASS effects: color correction/grayscale, background, 24/60 fps, quality modes, rotate+flip, sharpen/vignette, voice delay/speed/loudnorm and mono')
            # v0.10 local scene controls must affect pixels, not just the saved recipe.
            ffmpeg('-f','lavfi','-i','color=c=red:s=320x240:d=1','-f','lavfi','-i','color=c=blue:s=320x240:d=1','-filter_complex','[0:v][1:v]concat=n=2:v=1:a=0[v]','-map','[v]','-c:v','libx264','-pix_fmt','yuv420p',str(folder/'speed-colors.mp4'))
            speed_colors=upload(p,folder/'speed-colors.mp4')['id']
            tests=[('speed',[{'assetId':speed_colors,'duration':1,'options':{'speed':2}}]),('local-color',[{'assetId':clip,'duration':1,'options':{'brightness':-.3}},{'assetId':clip,'duration':1,'options':{'brightness':.3}}]),('local-fade',[{'assetId':clip,'duration':1,'options':{'fade':.4}}])]
            local={}
            for label,scenes in tests:
                job=request(f'/projects/{p}/renders',{'assetIds':[s['assetId'] for s in scenes],'scenes':scenes,'duration':len(scenes),'ratio':'16:9'});wait_job(p,job)
                output=folder/(label+'.mp4');urllib.request.urlretrieve(BASE+f'/jobs/{job["id"]}/download',output);local[label]=output
            assert pixel_at(local['speed'],.2,640,360)[0]>180 and pixel_at(local['speed'],.7,640,360)[2]>180
            assert sum(pixel_at(local['local-color'],1.5,640,360))-sum(pixel_at(local['local-color'],.5,640,360))>150
            assert max(pixel_at(local['local-fade'],0,640,360))<10 and pixel_at(local['local-fade'],.5,640,360)[1]>80 and max(pixel_at(local['local-fade'],.96,640,360))<30
            ffmpeg('-f','lavfi','-i','color=c=red:s=320x240:d=1','-vf','drawbox=x=160:y=0:w=160:h=240:c=blue:t=fill','-c:v','libx264','-pix_fmt','yuv420p',str(folder/'focus.mp4'))
            focus=upload(p,folder/'focus.mp4')['id'];scenes=[{'assetId':focus,'duration':1,'options':{'fit':'cover','zoom':1.2,'focusX':x}} for x in [0,1]]
            job=request(f'/projects/{p}/renders',{'assetIds':[focus,focus],'scenes':scenes,'duration':2,'ratio':'9:16'});wait_job(p,job)
            focused=folder/'focused.mp4';urllib.request.urlretrieve(BASE+f'/jobs/{job["id"]}/download',focused)
            assert pixel_at(focused,.5,360,640)[0]>180 and pixel_at(focused,1.5,360,640)[2]>180
            def caption_pixels(file):
                raw=subprocess.check_output(['ffmpeg','-hide_banner','-loglevel','error','-ss','0.5','-i',str(file),'-frames:v','1','-f','rawvideo','-pix_fmt','rgb24','pipe:1'])
                red=[];green=0
                for i in range(0,len(raw),3):
                    r,g,b=raw[i:i+3]
                    if r>170 and g<70 and b<70:red.append((i//3)//1280)
                    if g>180 and r<70 and b<70:green+=1
                return red,green
            for alignment in [8,2]:
                job=request(f'/projects/{p}/renders',{'assetIds':[clip],'duration':1,'ratio':'16:9','subtitleId':sub,'captions':{'size':28,'font':'mono','color':'#ff0000','outline':0,'shadow':0,'bold':True,'alignment':alignment,'margin':10}});wait_job(p,job)
                output=folder/('caption-'+str(alignment)+'.mp4');urllib.request.urlretrieve(BASE+f'/jobs/{job["id"]}/download',output);red,_=caption_pixels(output)
                assert len(red)>100 and (max(red)<360 if alignment==8 else min(red)>360),(alignment,len(red),min(red) if red else None)
            job=request(f'/projects/{p}/renders',{'assetIds':[clip],'duration':1,'ratio':'16:9','subtitleId':sub,'captions':{'size':28,'box':True,'boxColor':'#00ff00','boxOpacity':1,'outline':3,'alignment':5}});wait_job(p,job)
            boxed=folder/'boxed.mp4';urllib.request.urlretrieve(BASE+f'/jobs/{job["id"]}/download',boxed);_,green=caption_pixels(boxed);assert green>500,green
            print('PASS scene speed/brightness/cache/fade/cover-focus/zoom and actual caption color, position, font/bold and background box pixels')
            # v0.11: decode PCM to validate DSP instead of only checking recipe fields.
            for name,frequency in [('low',100),('high',5000),('loud',440)]:
                args=['-f','lavfi','-i',f'sine=frequency={frequency}:sample_rate=48000:duration=2']
                if name=='loud':args+=['-af','volume=6']
                ffmpeg(*args,'-c:a','pcm_s16le',str(folder/(name+'.wav')))
            ffmpeg('-f','lavfi','-i','anoisesrc=color=white:amplitude=0.01:sample_rate=48000:duration=2','-c:a','pcm_s16le',str(folder/'noise.wav'))
            sources={name:upload(p,folder/(name+'.wav'))['id'] for name in ['low','high','loud','noise']}
            def audio_job(name,options=None,music=False,duration=2):
                body={'assetIds':[clip],'mode':'loop','duration':duration,'ratio':'16:9','audio':options or {}}
                body['musicId' if music else 'voiceId']=sources[name]
                if music:body['musicGain']=1
                job=request(f'/projects/{p}/renders',body);wait_job(p,job);output=folder/('dsp-'+job['id']+'.mp4');urllib.request.urlretrieve(BASE+f'/jobs/{job["id"]}/download',output);return output
            low=audio_job('low');high=audio_job('high');loud=audio_job('loud');noise=audio_job('noise')
            hp=audio_job('low',{'voiceHighpass':400});lp=audio_job('high',{'voiceLowpass':1000});bass=audio_job('low',{'voiceBass':9});treble=audio_job('high',{'voiceTreble':-9});compressed=audio_job('loud',{'compressVoice':True});cleaned=audio_job('noise',{'denoiseVoice':True})
            assert audio_rms(hp,.5,.5)<audio_rms(low,.5,.5)*.4
            assert audio_rms(lp,.5,.5)<audio_rms(high,.5,.5)*.3
            assert audio_rms(bass,.5,.5)>audio_rms(low,.5,.5)*1.5
            assert audio_rms(treble,.5,.5)<audio_rms(high,.5,.5)*.7
            assert audio_rms(compressed,.5,.5)<audio_rms(loud,.5,.5)*.75
            assert audio_rms(cleaned,.8,.5)<audio_rms(noise,.8,.5)*.8
            def stereo_rms(file,start,duration):
                raw=subprocess.check_output(['ffmpeg','-hide_banner','-loglevel','error','-ss',str(start),'-i',str(file),'-t',str(duration),'-map','0:a:0','-ac','2','-ar','16000','-f','f32le','pipe:1'])
                values=struct.unpack('<'+'f'*(len(raw)//4),raw)
                return tuple(math.sqrt(sum(v*v for v in values[channel::2])/len(values[channel::2])) for channel in [0,1])
            pan=audio_job('low',{'voicePan':1});left,right=stereo_rms(pan,.5,.5);assert right>.03 and left<right*.1
            faded_voice=audio_job('low',{'voiceFadeIn':.5,'voiceFadeOut':.5});assert audio_rms(faded_voice,0,.1)<audio_rms(faded_voice,.8,.2)*.3 and audio_rms(faded_voice,1.9,.1)<audio_rms(faded_voice,.8,.2)*.3
            music_clip=audio_job('low',{'musicLoop':False,'musicSpeed':2,'musicHighpass':60,'musicLowpass':8000,'musicPan':-1,'musicFadeIn':.2,'musicFadeOut':.2},music=True);left,right=stereo_rms(music_clip,.4,.2);assert left>.03 and right<left*.1
            assert abs(float(probe(music_clip)['format']['duration'])-2)<.15 and audio_rms(music_clip,1.4,.2)<.003
            assert audio_rms(music_clip,0,.05)<audio_rms(music_clip,.4,.1)*.4 and audio_rms(music_clip,.95,.05)<audio_rms(music_clip,.4,.1)*.4
            ffmpeg('-f','lavfi','-i','sine=frequency=100:sample_rate=48000:duration=1','-f','lavfi','-i','sine=frequency=5000:sample_rate=48000:duration=1','-filter_complex','[0:a][1:a]concat=n=2:v=0:a=1[a]','-map','[a]','-c:a','pcm_s16le',str(folder/'trim-voice.wav'))
            sources['trim']=upload(p,folder/'trim-voice.wav')['id'];trim_voice=audio_job('trim',{'voiceStart':1},duration=1)
            raw=subprocess.check_output(['ffmpeg','-hide_banner','-loglevel','error','-ss','0.2','-i',str(trim_voice),'-t','0.5','-map','0:a:0','-ac','1','-ar','16000','-f','f32le','pipe:1']);values=struct.unpack('<'+'f'*(len(raw)//4),raw);crossings=sum((a>=0)!=(b>=0) for a,b in zip(values,values[1:]));assert crossings>2000,crossings
            print('PASS audio DSP: high/low pass, bass/treble, denoise/compressor RMS, stereo balance, voice trim/fades and music speed/nonloop/padding/fades')
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
