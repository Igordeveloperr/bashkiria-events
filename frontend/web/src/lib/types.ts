export interface City { id: string; name: string }
export interface Venue { id: string; cityId: string; name: string; address: string; aliases: { id: string; name: string; address?: string }[] }
export interface Category { id: string; name: string }
export interface EventSource { sourceId: string; url: string | null; checkedAt: string; type: string }
export interface EventItem {
  id: string; title: string; category: string; tags: string[]; startDate: string | null; startTime: string | null;
  endDate: string | null; endTime: string | null; dateStart: string | null; dateEnd: string | null; durationMin: number | null;
  city: string; cityId: string | null; venue: string; venueId: string | null; address: string; description: string;
  latitude: string | null; longitude: string | null; priceMin: string | null; priceMax: string | null; isFree: boolean | null;
  ageLimit: number | null; language: string | null; orgLink: string | null; imageUrl: string | null;
  status: string; sourceStatus: string; stale: boolean; verifiedAt: string; sources: EventSource[];
}
export interface Session { csrfToken: string; isAdmin: boolean; telegram: boolean; name: string | null; telegramAvailable: boolean }
export interface Page<T> { total: number; page: number; pageSize: number; items: T[] }
export interface EventsPage { total: number; page: number; pages: number; items: EventItem[] }
export interface Subscription { id: string; cityId: string; category: string | null; dateFrom: string | null; dateTo: string | null; channel: string; version: number }
export interface Notice { id: string; channel: string; status: string; createdAt: string; readAt: string | null; events: Pick<EventItem, 'id' | 'title' | 'category' | 'startDate' | 'startTime' | 'city' | 'venue' | 'address' | 'priceMin' | 'priceMax' | 'ageLimit'>[] }
export interface Feedback { id: string; kind: string; subject: string; message: string; status: string; createdAt: string; version: number }
export interface ParserEvent extends Omit<EventItem, 'priceMin' | 'priceMax' | 'sources' | 'stale'> {
  priceMin: number | null; priceMax: number | null; version: number; issues: { code: string; message: string }[];
  confidence: number; sourceType: string;
}
export interface Source { id: string; kind: string; enabled: boolean; listingUrl: string; priority: number }
export interface Run { id: string; sourceId: string; status: string; createdAt: string; error: string | null; report: { addedEvents: number; updatedEvents: number; unchangedEvents: number; errors: { message: string }[] } | null }
export interface ImportReport { total: number; added: number; updated: number; unchanged: number; rejected: number; items: { row: number; outcome: string; errors: { message: string }[]; issues: { message: string }[] }[] }
