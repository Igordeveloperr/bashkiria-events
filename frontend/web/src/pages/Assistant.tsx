import { useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { useQueries, useQueryClient } from '@tanstack/react-query';
import { ArrowRight, Plus, Send, Sparkles } from 'lucide-react';
import { api, RequestError, send } from '../lib/api';
import { useSession } from '../lib/store';
import type { EventItem } from '../lib/types';
import { BackLink, ErrorState, Modal } from '../components/ui';
import { EventCard } from '../components/EventCard';

type Message = { role: 'user' | 'assistant'; text: string; traceId?: string | null };
type Pending = { message: string; requestId: string; subscriptionConsent: boolean };
function Answer({ message }: { message: Message }) {
  const ids = [...new Set(message.text.match(/\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi) ?? [])].slice(0, 5);
  const events = useQueries({ queries: ids.map(id => ({ queryKey: ['event', id], queryFn: () => api<EventItem>(`/api/events/${id}`), retry: false })) });
  // Untrusted model output stays text; cards are resolved against the public catalog.
  return <><p className="chat-text">{message.text}</p>{events.some(e => e.data) && <div className="chat-cards">{events.map((e, i) => e.data && <EventCard key={ids[i]} event={e.data} compact/>)}</div>}{message.traceId && <details><summary>Идентификатор ответа</summary><code>{message.traceId}</code></details>}</>;
}
export function Assistant() {
  const session = useSession(); const cache = useQueryClient();
  const [sessionId, setSessionId] = useState(() => crypto.randomUUID()); const [messages, setMessages] = useState<Message[]>([]);
  const [input, setInput] = useState(''); const [pending, setPending] = useState<Pending | null>(null); const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Error | null>(null); const [confirm, setConfirm] = useState(false);
  async function deliver(item: Pending, fresh = false) {
    setBusy(true); setError(null); setPending(item);
    if (fresh) { setMessages(m => [...m, { role: 'user', text: item.message }]); setInput(''); }
    try {
      const data = await send<{ reply: string; traceId: string | null }>('/api/chat', { ...item, sessionId });
      setMessages(m => [...m, { role: 'assistant', text: data.reply, traceId: data.traceId }]); setPending(null);
      void cache.invalidateQueries({ queryKey: ['me'] });
    } catch (e) { setError(e instanceof Error ? e : new Error('Ответ не получен.'));
      if (e instanceof RequestError && e.code === 'generation_failed') setPending({ ...item, requestId: crypto.randomUUID() });
    } finally { setBusy(false); }
  }
  function submit(e: FormEvent) { e.preventDefault(); if (input.trim() && !busy && !pending) void deliver({ message: input.trim(), requestId: crypto.randomUUID(), subscriptionConsent: false }, true); }
  function reset() { setSessionId(crypto.randomUUID()); setMessages([]); setPending(null); setError(null); setInput(''); }
  return <div className="container standard-page assistant-page"><BackLink/><div className="page-title title-with-action"><div><span className="eyebrow blue">Ваш культурный помощник</span><h1>С чего начнём выходные?</h1><p>Расскажите о планах — найдём события по данным афиши.</p></div><button className="button secondary" disabled={busy} onClick={reset}><Plus size={17}/>Новый диалог</button></div>
    <div className="assistant-layout"><section className="chat-panel" aria-label="Диалог с помощником"><div className="chat-messages" role="log" aria-live="polite" aria-relevant="additions">
      {!messages.length && <div className="chat-welcome"><Sparkles size={38}/><h2>Хорошие планы начинаются с вопроса</h2><p>Укажите город, дату, возраст и бюджет.<br/>Можно уточнять: «подешевле» или «а что завтра?»</p><div className="chat-suggestions">{['Куда пойти в Уфе в выходные?', 'Что есть для ребёнка 8 лет до 1000 рублей?', 'Покажи бесплатные выставки'].map(text => <button key={text} onClick={() => setInput(text)}>{text}<ArrowRight size={15}/></button>)}</div></div>}
      {messages.map((message, i) => <article key={i} className={`chat-message ${message.role}`}><span className="eyebrow">{message.role === 'user' ? 'Вы' : 'Помощник по афише'}</span>{message.role === 'assistant' ? <Answer message={message}/> : <p className="chat-text">{message.text}</p>}</article>)}
      {busy && <p role="status"><Sparkles size={17}/> Проверяем афишу и подбираем варианты…</p>}</div>
      {error && <div className="chat-error" role="alert"><p>{error.message}</p><p>Продолжайте подбор в <Link to="/">обычном каталоге</Link> или повторите запрос.</p><button className="button secondary" disabled={busy || !pending} onClick={() => pending && void deliver(pending)}>Повторить</button></div>}
      {session.isError && <ErrorState error={session.error} retry={() => void session.refetch()}/>}
      <form className="chat-composer" onSubmit={submit}><label className="sr-only" htmlFor="chat-input">Ваш запрос</label><textarea id="chat-input" rows={2} maxLength={4000} placeholder="Например: концерт в Уфе в субботу до 1500 ₽" value={input} onChange={e => setInput(e.target.value)} disabled={busy || Boolean(pending)} onKeyDown={e => { if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) { e.preventDefault(); e.currentTarget.form?.requestSubmit(); } }}/><button className="icon-button" aria-label="Отправить запрос" disabled={!input.trim() || busy || !session.data || Boolean(pending)}><Send size={21}/></button></form>
      <div className="chat-actions"><small>До двух минут на ответ. Shift + Enter — новая строка.</small><button className="text-link" disabled={busy || !messages.length || Boolean(pending) || !session.data} onClick={() => setConfirm(true)}>Подтвердить подписку из диалога</button></div>
    </section><aside className="panel assistant-help"><Sparkles size={25}/><h2>По данным сервиса</h2><p>Помощник ищет события в нашей афише и уточняет условия. Откройте карточку, чтобы проверить источник, дату и цену.</p><p>Для подписки нужно отдельное подтверждение. Оно действует только для следующего запроса.</p><Link className="button secondary" to="/">Искать через фильтры<ArrowRight size={16}/></Link><p><small>Не отправляйте личные данные. Переписка обрабатывается сервисом ИИ; на странице отображается до закрытия или обновления.</small></p></aside></div>
    {confirm && <Modal title="Разрешить подписку?" onClose={() => setConfirm(false)}><p className="modal-description">Разрешить помощнику создать подписку на город, категорию и период, обсуждённые в этом диалоге? Если условия не уточнены, воспользуйтесь формой подписки.</p><div className="modal-actions"><Link className="button secondary" to="/subscriptions">Выбрать вручную</Link><button className="button primary" onClick={() => { setConfirm(false); void deliver({ message: 'Подтверждаю подписку на обсуждённые город, категорию и период. Если параметры не определены, сначала уточни их.', requestId: crypto.randomUUID(), subscriptionConsent: true }, true); }}>Подтверждаю</button></div></Modal>}
  </div>;
}
