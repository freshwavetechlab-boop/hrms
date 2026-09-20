import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Space, Switch, Tag } from 'antd'
import type { InternalInterviewEvent } from '../types/internalInterviews'
import type { InterviewSessionClient } from '../services/internalInterviewService'
import { InterviewVoicePlayback, type InterviewVoicePublisher } from '../services/interviewVoicePlayback'

export default function InterviewVoiceControls({ client, question, answerSeconds, publishVoice, onDraft, onError }: {
  client: InterviewSessionClient; question?: InternalInterviewEvent; answerSeconds: number;
  publishVoice?: InterviewVoicePublisher;
  onDraft: (text: string) => void; onError: (error: string) => void;
}) {
  const [recording, setRecording] = useState(false)
  const [parsing, setParsing] = useState(false)
  const [speaking, setSpeaking] = useState(false)
  const [autoVoice, setAutoVoice] = useState(false)
  const [elapsed, setElapsed] = useState(0)
  const [acquiring, setAcquiring] = useState(false)
  const opening = useRef(false)
  const voiceRequest = useRef<AbortController | null>(null)
  const parseRequest = useRef<AbortController | null>(null)
  const recorder = useRef<MediaRecorder | null>(null)
  const mic = useRef<MediaStream | null>(null)
  const audio = useRef<InterviewVoicePlayback | null>(null)
  const [voiceDelivery, setVoiceDelivery] = useState<'room' | 'local' | null>(null)
  const timer = useRef<number | undefined>(undefined)
  const mounted = useRef(true)
  const latest = useRef(question?.id); latest.current = question?.id
  const callbacks = useRef({ onDraft, onError }); callbacks.current = { onDraft, onError }
  const lastSpoken = useRef<number | undefined>(undefined)
  const previousPublisher = useRef(publishVoice)
  const stopAudio = () => { audio.current?.stop(); audio.current = null }
  useEffect(() => { mounted.current = true; return () => {
    mounted.current = false; window.clearInterval(timer.current)
    voiceRequest.current?.abort(); parseRequest.current?.abort()
    if (recorder.current?.state === 'recording') { recorder.current.onstop = null; recorder.current.stop() }
    mic.current?.getTracks().forEach(track => track.stop()); stopAudio()
  } }, [])
  useEffect(() => {
    voiceRequest.current?.abort(); parseRequest.current?.abort(); stopAudio(); setSpeaking(false)
    if (recorder.current?.state === 'recording') recorder.current.stop()
  }, [question?.id])
  useEffect(() => {
    if (previousPublisher.current && !publishVoice && audio.current) {
      voiceRequest.current?.abort(); stopAudio(); setSpeaking(false); setVoiceDelivery(null)
      callbacks.current.onError('The call disconnected during interviewer voice. Rejoin and use Hear question to replay it in the call.')
    }
    previousPublisher.current = publishVoice
  }, [publishVoice])
  const play = async () => {
    if (!question || speaking || recording) return
    setSpeaking(true); setVoiceDelivery(null); stopAudio(); lastSpoken.current = question.id
    const pending = new AbortController(); voiceRequest.current = pending
    let playback: InterviewVoicePlayback | undefined
    try {
      playback = new InterviewVoicePlayback(); audio.current = playback
      await playback.resume()
      if (pending.signal.aborted || !mounted.current || latest.current !== question.id) { playback.stop(); return }
      const blob = await client.speak(question.id, pending.signal)
      if (!mounted.current || latest.current !== question.id || audio.current !== playback) { playback.stop(); return }
      const shared = await playback.play(blob, publishVoice, () => { if (mounted.current) setSpeaking(false); if (audio.current === playback) audio.current = null })
      if (mounted.current && latest.current === question.id && !pending.signal.aborted) setVoiceDelivery(shared ? 'room' : 'local')
    } catch (error) { playback?.stop(); if (audio.current === playback) audio.current = null; if (mounted.current && latest.current === question.id && !pending.signal.aborted) callbacks.current.onError(error instanceof Error ? error.message : 'Local voice is unavailable. Read the question on screen.') }
    finally { if (mounted.current && latest.current === question.id && !audio.current) setSpeaking(false) }
  }
  useEffect(() => { if (autoVoice && question && lastSpoken.current !== question.id && !recording && !speaking) void play() }, [autoVoice, question?.id, recording, speaking])
  const start = async () => {
    if (!question || question.kind !== 'question' || opening.current || recording || parsing) return
    opening.current = true; setAcquiring(true); voiceRequest.current?.abort()
    stopAudio(); setSpeaking(false)
    try {
      if (!navigator.mediaDevices?.getUserMedia || typeof MediaRecorder === 'undefined') throw new Error('This browser does not support voice recording. Type your answer.')
      const type = ['audio/webm;codecs=opus', 'audio/ogg;codecs=opus', 'audio/mp4'].find(value => MediaRecorder.isTypeSupported(value))
      if (!type) throw new Error('No supported voice-recording format. Type your answer.')
      mic.current = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true }, video: false })
      if (!mounted.current || latest.current !== question.id) { mic.current.getTracks().forEach(track => track.stop()); return }
      const current = new MediaRecorder(mic.current, { mimeType: type, audioBitsPerSecond: 64000 }); recorder.current = current
      const chunks: Blob[] = []; let bytes = 0
      current.ondataavailable = event => { if (event.data.size) { bytes += event.data.size; chunks.push(event.data) }; if (bytes > 12 * 1024 * 1024 && current.state === 'recording') current.stop() }
      current.onstop = async () => {
        window.clearInterval(timer.current); mic.current?.getTracks().forEach(track => track.stop())
        if (!mounted.current) return
        setRecording(false)
        if (latest.current !== question.id) { callbacks.current.onError('The question changed while recording. No answer was submitted.'); return }
        if (bytes > 12 * 1024 * 1024) { callbacks.current.onError('Voice clip exceeded 12 MB. Please type the answer or use a shorter clip.'); return }
        setParsing(true)
        const pending = new AbortController(); parseRequest.current = pending
        try { const draft = await client.transcribe(question.id, new Blob(chunks, { type }), pending.signal); if (mounted.current && latest.current === question.id && !pending.signal.aborted) callbacks.current.onDraft(draft.text) }
        catch (error) { if (mounted.current && latest.current === question.id && !pending.signal.aborted) callbacks.current.onError(error instanceof Error ? error.message : 'Voice parsing failed. No answer was submitted.') }
        finally { if (mounted.current) setParsing(false) }
      }
      current.start(1000); setRecording(true); setElapsed(0)
      const began = Date.now()
      timer.current = window.setInterval(() => { const seconds = Math.floor((Date.now() - began) / 1000); setElapsed(seconds); if (seconds >= answerSeconds && current.state === 'recording') current.stop() }, 500)
    } catch (error) { mic.current?.getTracks().forEach(track => track.stop()); if (mounted.current) callbacks.current.onError(error instanceof Error ? error.message : 'Microphone unavailable. Type your answer.') }
    finally { opening.current = false; if (mounted.current) setAcquiring(false) }
  }
  return <section className="interview-voice-controls">
    <Space wrap><Switch checked={autoVoice} onChange={setAutoVoice} aria-label="Speak questions with local voice" /><span>Speak questions with local voice</span>
      <Button disabled={!question || recording} loading={speaking} onClick={() => void play()}>Hear question</Button>
      <Button disabled={!question || question.kind !== 'question' || parsing || acquiring} danger={recording} loading={parsing || acquiring} onClick={() => recording ? recorder.current?.stop() : void start()}>{recording ? 'Stop and parse voice' : 'Record answer'}</Button>
      {recording && <Tag color="red">Recording {elapsed}s / {answerSeconds}s</Tag>}
      {voiceDelivery && <Tag color={voiceDelivery === 'room' ? 'green' : 'orange'}>{voiceDelivery === 'room' ? 'Interviewer voice shared in call' : 'Local playback only — join the call to share interviewer voice'}</Tag>}
    </Space>
    {parsing && <Alert type="info" showIcon message="Local speech is transcribing your answer… Nothing has been submitted yet." />}
    <small>Voice creates an editable draft below. Review it, then click Submit answer. You can always type instead.</small>
  </section>
}
