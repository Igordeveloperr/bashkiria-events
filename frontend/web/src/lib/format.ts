import type { EventItem } from './types';
export const categories: Record<string, string> = { concert: 'Концерты', theatre: 'Театр', exhibition: 'Выставки', festival: 'Фестивали', cinema: 'Кино', excursion: 'Экскурсии', workshop: 'Мастер-классы', lecture: 'Лекции', other: 'Другое' };
export const categorySingle: Record<string, string> = { concert: 'Концерт', theatre: 'Спектакль', exhibition: 'Выставка', festival: 'Фестиваль', cinema: 'Кино', excursion: 'Экскурсия', workshop: 'Мастер-класс', lecture: 'Лекция', other: 'Событие' };
export function localDate(now = new Date()): string {
  const parts = new Intl.DateTimeFormat('en-US', { timeZone: 'Asia/Yekaterinburg', year: 'numeric', month: '2-digit', day: '2-digit' }).formatToParts(now);
  const get = (key: string) => parts.find(p => p.type === key)!.value;
  return `${get('year')}-${get('month')}-${get('day')}`;
}
export function addDays(day: string, offset: number): string { const date = new Date(`${day}T12:00:00Z`); date.setUTCDate(date.getUTCDate() + offset); return date.toISOString().slice(0, 10); }
export function period(day: string): { from?: string; to?: string } {
  const today = localDate(); const weekday = new Date(`${today}T12:00:00Z`).getUTCDay();
  if (day === 'today') return { from: today, to: today };
  if (day === 'tomorrow') return { from: addDays(today, 1), to: addDays(today, 1) };
  if (day === 'week') return { from: today, to: addDays(today, 6) };
  if (day === 'weekend') { const saturday = addDays(today, weekday === 0 ? -1 : (6 - weekday + 7) % 7); return { from: weekday === 0 ? today : saturday, to: addDays(saturday, 1) }; }
  return {};
}
export function dateLabel(value?: string | null, options: Intl.DateTimeFormatOptions = { day: 'numeric', month: 'long' }): string {
  if (!value) return 'Дата уточняется';
  return new Intl.DateTimeFormat('ru-RU', { timeZone: 'Asia/Yekaterinburg', ...options }).format(new Date(value.length === 10 ? `${value}T12:00:00+05:00` : value));
}
export function eventDate(e: Pick<EventItem, 'startDate' | 'startTime'>): string { return dateLabel(e.startDate) + (e.startTime ? ` · ${e.startTime.slice(0, 5)}` : ' · Время уточняется'); }
export function price(e: Pick<EventItem, 'isFree' | 'priceMin' | 'priceMax'> | { priceMin: string | null; priceMax: string | null }): string {
  if ('isFree' in e && e.isFree === true || e.priceMin !== null && Number(e.priceMin) === 0 && e.priceMax !== null && Number(e.priceMax) === 0) return 'Бесплатно';
  if (e.priceMin === null) return 'Цена уточняется';
  return `${Number(e.priceMin) > 0 ? 'от ' : ''}${Number(e.priceMin).toLocaleString('ru-RU')} ₽`;
}
export const feedbackStatus: Record<string, string> = { new: 'Получено', inProgress: 'В работе', resolved: 'Решено', rejected: 'Отклонено' };
