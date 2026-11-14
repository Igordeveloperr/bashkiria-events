import { ArrowUpRight, Heart, MapPin } from 'lucide-react';
import { Link } from 'react-router-dom';
import type { EventItem } from '../lib/types';
import { categorySingle, eventDate, price } from '../lib/format';
import { useFavorites } from '../lib/store';

export function EventArtwork({ event, large = false }: { event: EventItem; large?: boolean }) {
  return <div className={`event-art art-${event.category} ${large ? 'large' : ''}`}>
    <div className="poster-pattern" aria-hidden="true"><i/><i/><i/><i/><span>{categorySingle[event.category] ?? 'Культура'}</span></div>
    {event.imageUrl && <img src={event.imageUrl} alt={`Афиша: ${event.title}`} loading={large ? 'eager' : 'lazy'} referrerPolicy="no-referrer" onError={e => { e.currentTarget.style.display = 'none'; }}/>}</div>;
}
export function EventCard({ event, compact = false, external = false }: { event: EventItem; compact?: boolean; external?: boolean }) {
  const favorites = useFavorites(); const saved = favorites.ids.includes(event.id);
  const linkProps = external ? { target: '_blank', rel: 'noopener noreferrer' } : {};
  return <article className={`event-card ${compact ? 'compact' : ''}`}>
    <div className="event-cover"><Link {...linkProps} to={`/events/${event.id}`} aria-label={`Открыть: ${event.title}`}><EventArtwork event={event}/></Link>
      <span className="event-category">{event.status === 'cancelled' || event.sourceStatus === 'cancelled' ? 'Отменено' : event.sourceStatus === 'rescheduled' ? 'Перенесено' : categorySingle[event.category] ?? 'Событие'}</span>
      <button className={`favorite-button ${saved ? 'saved' : ''}`} onClick={() => favorites.toggle(event.id)} aria-label={saved ? `Убрать из избранного: ${event.title}` : `В избранное: ${event.title}`} aria-pressed={saved}><Heart size={19} fill={saved ? 'currentColor' : 'none'}/></button>
    </div>
    <div className="event-card-body"><div className="event-date">{eventDate(event)}</div><h3><Link {...linkProps} to={`/events/${event.id}`}>{event.title}</Link></h3>
      <div className="event-location"><MapPin size={15}/><span>{event.city}{event.venue ? ` · ${event.venue}` : ''}</span></div>
      <div className="event-card-bottom"><strong className={event.isFree ? 'price free' : 'price'}>{price(event)}</strong>{event.ageLimit !== null && <span className="age-label">{event.ageLimit}+</span>}<Link {...linkProps} className="card-arrow" to={`/events/${event.id}`} aria-label={`Подробнее: ${event.title}`}><ArrowUpRight size={20}/></Link></div>
    </div>
  </article>;
}
