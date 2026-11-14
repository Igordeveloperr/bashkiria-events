import { useState } from 'react';
import { useParams, Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { ArrowRight, ArrowUpRight, Bell, CalendarDays, CheckCircle2, Clock3, ExternalLink, Heart, Info, MapPin, Share2, Ticket } from 'lucide-react';
import { api } from '../lib/api';
import type { EventItem } from '../lib/types';
import { categorySingle, dateLabel, eventDate, price } from '../lib/format';
import { useFavorites } from '../lib/store';
import { EventArtwork } from '../components/EventCard';
import { BackLink, ErrorState, Loading, useToast } from '../components/ui';

export function EventDetail() {
  const { id } = useParams(); const favorites = useFavorites(); const toast = useToast(); const [sharing, setSharing] = useState(false);
  const event = useQuery({ queryKey: ['event', id], queryFn: () => api<EventItem>(`/api/events/${id}`), refetchInterval: 60000 });
  if (event.isPending) return <div className="container"><Loading/></div>;
  if (event.isError) return <div className="container"><BackLink/><ErrorState error={event.error} retry={() => void event.refetch()}/></div>;
  const e = event.data; const saved = favorites.ids.includes(e.id);
  const cancelled = e.status === 'cancelled' || e.sourceStatus === 'cancelled'; const postponed = e.sourceStatus === 'rescheduled';
  const ended = Boolean(e.dateEnd && Date.parse(e.dateEnd) < Date.now());
  const origin = e.orgLink ?? e.sources.find(s => s.url)?.url;
  async function share() {
    setSharing(true);
    try { if (navigator.share) await navigator.share({ title: e.title, url: location.href }); else { await navigator.clipboard.writeText(location.href); toast('Ссылка на событие скопирована'); } }
    catch { /* Cancelling the system share dialog is not an error. */ }
    finally { setSharing(false); }
  }
  return <div className="container detail-page"><BackLink/>
    <div className="detail-layout"><div className="detail-main"><div className="detail-art"><EventArtwork event={e} large/><span className="detail-art-category">{categorySingle[e.category] ?? 'Событие'}</span>{e.ageLimit !== null && <span className="detail-age">{e.ageLimit}+</span>}</div>
      <div className="detail-title-row"><div><p className="eyebrow blue">{e.city} · {categorySingle[e.category]}</p><h1>{e.title}</h1></div><button className={`icon-button large ${saved ? 'saved' : ''}`} aria-label={saved ? 'Убрать из избранного' : 'Добавить в избранное'} aria-pressed={saved} onClick={() => favorites.toggle(e.id)}><Heart fill={saved ? 'currentColor' : 'none'}/></button></div>
      {(cancelled || postponed || ended || e.stale) && <div className={`notice ${cancelled ? 'danger' : ''}`} role="status"><Info size={20}/><div><strong>{cancelled ? 'Событие отменено' : postponed ? 'Событие перенесено' : ended ? 'Событие завершилось' : 'Данные требуют обновления'}</strong><p>{cancelled ? 'Карточка сохранена для справки. Посещение по указанным дате и времени не планируйте.' : 'Уточните актуальную информацию по ссылке организатора.'}</p></div></div>}
      <section className="description-section"><h2>О событии</h2><p className="event-description">{e.description || 'Организатор пока не добавил подробное описание. Уточните детали по ссылке на источник.'}</p>{e.tags.length > 0 && <div className="tag-list">{e.tags.map(tag => <span key={tag}>#{tag}</span>)}</div>}</section>
      <section className="venue-section"><div className="section-title"><h2>Место проведения</h2><MapPin size={21}/></div><h3>{e.venue || 'Площадка уточняется'}</h3><p>{e.city}{e.address ? `, ${e.address}` : ''}</p>{e.address && <a className="text-link" href={`https://yandex.ru/maps/?text=${encodeURIComponent(`${e.city}, ${e.address}`)}`} target="_blank" rel="noopener noreferrer">Открыть на карте<ArrowUpRight size={16}/></a>}</section>
      <section className="sources-section"><h2>Источники и актуальность</h2><p className="muted">Мы сохраняем сведения из открытых источников. Последняя проверка — {dateLabel(e.verifiedAt, { day: 'numeric', month: 'long', hour: '2-digit', minute: '2-digit' })}, время Уфы.</p>{e.sources.map((source, i) => <div className="source-row" key={source.sourceId + i}><div><CheckCircle2 size={17}/><span>{source.url ? new URL(source.url).hostname.replace('www.', '') : source.type === 'manual' ? 'Ручной ввод модератором' : 'Импорт организатора'}</span></div>{source.url && <a href={source.url} target="_blank" rel="noopener noreferrer" aria-label={`Открыть источник ${source.sourceId}`}><ExternalLink size={16}/></a>}</div>)}<Link className="text-link" to={`/feedback?eventId=${e.id}&kind=correction`}>Сообщить о неточности <ArrowRight size={15}/></Link></section>
    </div><aside className="detail-aside"><div className="booking-card"><span className="eyebrow blue">Ваш следующий план</span><h2>{price(e)}</h2><div className="booking-line"><CalendarDays size={20}/><div><strong>{dateLabel(e.startDate)}</strong><span>{e.startTime ? `Начало в ${e.startTime.slice(0, 5)}` : 'Время уточняется'}</span></div></div><div className="booking-line"><MapPin size={20}/><div><strong>{e.venue || 'Площадка уточняется'}</strong><span>{e.city}</span></div></div>{e.ageLimit !== null && <div className="booking-line"><Ticket size={20}/><span>Для гостей {e.ageLimit}+</span></div>}{e.durationMin && <div className="booking-line"><Clock3 size={20}/><span>{e.durationMin} мин.</span></div>}
      {origin && !cancelled && !postponed && !ended && <a className="button primary full" target="_blank" rel="noopener noreferrer" href={origin}>У организатора<ArrowUpRight size={18}/></a>}
      <button className="button secondary full" onClick={() => void share()} disabled={sharing}><Share2 size={17}/>Поделиться событием</button><p className="booking-note">Мы не продаём билеты. Цену и наличие мест подтверждает организатор.</p></div>
      <div className="aside-subscribe"><Bell size={24}/><h3>Больше похожих событий</h3><p>Соберём интересное по вашему городу и любимой категории.</p><Link className="text-link" to={`/subscriptions?city=${encodeURIComponent(e.city)}&category=${e.category}`}>Настроить подписку<ArrowRight size={16}/></Link></div>
    </aside></div><div className="mobile-detail-action"><span><small>{eventDate(e)}</small><strong>{price(e)}</strong></span>{origin && !cancelled && !postponed && !ended ? <a className="button primary" href={origin} target="_blank" rel="noopener noreferrer">Подробнее<ArrowUpRight size={17}/></a> : <Link className="button primary" to="/">К афише<ArrowRight size={17}/></Link>}</div>
  </div>;
}
