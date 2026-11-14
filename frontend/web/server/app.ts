import Fastify, { type FastifyReply, type FastifyRequest } from 'fastify';
import cookie from '@fastify/cookie';
import rateLimit from '@fastify/rate-limit';
import staticFiles from '@fastify/static';
import { createHash } from 'node:crypto';
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { z } from 'zod';
import { config, projectRoot, validateConfig } from './config.ts';
import { csrf, decodeSession, encodeSession, equal, newVisitor, validateTelegram, type Visitor } from './security.ts';

class ApiError extends Error {
  constructor(public status: number, public code: string, message: string, public errors: unknown[] = []) { super(message); }
}
const uuid = z.string().uuid();
const idParams = z.object({ id: uuid });
type ObjectData = Record<string, unknown>;
const text = (v: unknown): string => typeof v === 'string' ? v : '';
const url = (v: unknown): string | null => {
  try { const u = new URL(text(v)); return ['http:', 'https:'].includes(u.protocol) && !u.username && !u.password ? u.href : null; } catch { return null; }
};
const lower = (v: unknown): string => text(v).replace(/^./, x => x.toLowerCase());
function eventDto(e: ObjectData): ObjectData {
  return {
    id: e.id, title: e.title, category: lower(e.category), tags: e.tags, startDate: e.start_date, startTime: e.start_time,
    endDate: e.end_date, endTime: e.end_time, dateStart: e.date_start, dateEnd: e.date_end, durationMin: e.duration_min,
    city: e.city, cityId: e.city_id, venue: e.venue, venueId: e.venue_id, address: e.address,
    description: e.description, latitude: e.latitude, longitude: e.longitude, priceMin: e.price_min, priceMax: e.price_max,
    isFree: e.is_free, ageLimit: e.age_limit, language: e.language, orgLink: url(e.org_link), imageUrl: url(e.image_url),
    status: lower(e.status), sourceStatus: lower(e.source_status), verifiedAt: e.verified_at,
    stale: Date.now() - Date.parse(text(e.verified_at)) > 24 * 3600000,
    sources: ((e.sources ?? []) as ObjectData[]).map(s => ({ sourceId: s.source_id, url: url(s.source_url), checkedAt: s.checked_at, type: lower(s.source_type) })),
  };
}
export async function createServer() {
  const secret = validateConfig();
  const app = Fastify({ logger: { level: 'warn', redact: ['req.headers.cookie', 'req.headers.authorization', 'res.headers.set-cookie'] },
    disableRequestLogging: true, bodyLimit: 6 * 1024 * 1024, trustProxy: ['127.0.0.1', '::1', '172.28.50.0/24'] });
  await app.register(cookie);
  await app.register(rateLimit, { max: 150, timeWindow: '1 minute' });
  app.addContentTypeParser('text/csv', { parseAs: 'string', bodyLimit: 5 * 1024 * 1024 }, (_req, body, done) => done(null, body));
  app.setErrorHandler((error: Error & { statusCode?: number }, req, reply) => {
    const status = error instanceof ApiError ? error.status : error instanceof z.ZodError ? 400 : error.statusCode ?? 500;
    const code = error instanceof ApiError ? error.code : status === 429 ? 'rate_limit_exceeded' : status < 500 ? 'validation_error' : 'internal_error';
    const message = error instanceof ApiError ? error.message : status === 429 ? 'Слишком много запросов. Попробуйте чуть позже.' : status < 500 ? 'Проверьте введённые данные.' : 'Не удалось выполнить запрос. Попробуйте ещё раз.';
    reply.header('Cache-Control', 'no-store').code(status).send({ type: `urn:bashkiria-events:error:${code}`, title: message, status, code,
      instance: req.url.split('?')[0], traceId: req.id, errors: error instanceof ApiError ? error.errors : error instanceof z.ZodError ? error.issues.map(i => ({ field: i.path.join('.'), message: i.message })) : [] });
  });
  function visitor(req: FastifyRequest, reply: FastifyReply): Visitor {
    const current = decodeSession(req.cookies.be_session, secret);
    if (current) return current;
    const created = newVisitor(); setVisitor(reply, created); return created;
  }
  function setVisitor(reply: FastifyReply, v: Visitor) {
    reply.setCookie('be_session', encodeSession(v, secret), { httpOnly: true, sameSite: 'lax', secure: config.publicOrigin.startsWith('https:'), path: '/', maxAge: 90 * 86400 });
  }
  function requireAdmin(req: FastifyRequest, reply: FastifyReply): Visitor {
    const v = visitor(req, reply);
    if (!v.adminUntil || v.adminUntil < Date.now()) throw new ApiError(401, 'admin_required', 'Войдите в панель модератора.');
    return v;
  }
  app.addHook('onRequest', async (req, reply) => {
    reply.header('X-Content-Type-Options', 'nosniff').header('Referrer-Policy', 'strict-origin-when-cross-origin');
    if (req.url.startsWith('/api/') && !['GET', 'HEAD', 'OPTIONS'].includes(req.method)) {
      const origin = req.headers.origin;
      if (origin && origin !== config.publicOrigin && new URL(origin).host !== req.headers.host) throw new ApiError(403, 'forbidden', 'Запрос с другого сайта запрещён.');
      const v = decodeSession(req.cookies.be_session, secret);
      if (!v || !equal(text(req.headers['x-csrf-token']), csrf(v, secret))) throw new ApiError(403, 'csrf_required', 'Обновите страницу и повторите действие.');
    }
    if (req.url.startsWith('/api/admin/')) { requireAdmin(req, reply); reply.header('Cache-Control', 'no-store'); }
    if (req.url.startsWith('/api/me/') || req.url.startsWith('/api/session')) reply.header('Cache-Control', 'no-store');
  });
  async function upstream(path: string, mode: 'public' | 'parser' | 'user' | 'admin', options: { method?: string; body?: unknown; raw?: boolean; contentType?: string; key?: string; user?: Visitor } = {}): Promise<unknown> {
    const headers: Record<string, string> = { Accept: 'application/json' };
    if (mode === 'parser') headers['X-Parser-Key'] = config.parserKey;
    if (mode === 'user') { headers['X-Interaction-Key'] = config.userKey; headers['X-User-Id'] = options.user!.id; }
    if (mode === 'admin') { headers['X-Interaction-Admin-Key'] = config.adminKey; headers['X-Actor-Id'] = 'web-admin'; }
    if (options.key) headers['Idempotency-Key'] = options.key;
    if (options.body !== undefined) headers['Content-Type'] = options.contentType ?? 'application/json';
    let response: Response;
    try { response = await fetch(new URL(path, config.gateway), { method: options.method ?? 'GET', headers,
      body: options.body === undefined ? undefined : options.raw ? String(options.body) : JSON.stringify(options.body), signal: AbortSignal.timeout(30000), redirect: 'error' }); }
    catch { throw new ApiError(503, 'upstream_unavailable', 'Сервис временно недоступен. Попробуйте ещё раз.'); }
    const payload = response.status === 204 ? null : await response.json().catch(() => null) as ObjectData | null;
    if (!response.ok) {
      const title = payload && (text(payload.title) || text(payload.detail));
      throw new ApiError(response.status, text(payload?.code) || 'request_failed', title || (response.status === 404 ? 'Запись не найдена.' : 'Не удалось выполнить действие.'), Array.isArray(payload?.errors) ? payload.errors : []);
    }
    return payload;
  }
  const parser = (path: string, options = {}) => upstream(`/api/v1/parser${path}`, 'parser', options);
  async function references(path: string): Promise<ObjectData[]> {
    const result: ObjectData[] = [];
    for (let page = 1; page <= 20; page++) {
      const separator = path.includes('?') ? '&' : '?';
      const data = await parser(`/reference/${path}${separator}page=${page}&pageSize=100`) as { items: ObjectData[]; total: number };
      result.push(...data.items); if (result.length >= data.total) break;
    }
    return result;
  }
  app.get('/api/session', async (req, reply) => {
    const v = visitor(req, reply); return { csrfToken: csrf(v, secret), isAdmin: Boolean(v.adminUntil && v.adminUntil > Date.now()),
      telegram: Boolean(v.telegram), name: v.telegramName ?? null, telegramAvailable: Boolean(config.botToken) };
  });
  app.post('/api/session/telegram', async (req, reply) => {
    if (!config.botToken) throw new ApiError(501, 'telegram_not_configured', 'Telegram-вход будет доступен после подключения бота.');
    const { initData } = z.object({ initData: z.string().max(16000) }).strict().parse(req.body);
    let v: Visitor; try { v = validateTelegram(initData, config.botToken); } catch { throw new ApiError(401, 'invalid_telegram_data', 'Не удалось подтвердить запуск из Telegram. Откройте приложение заново.'); }
    setVisitor(reply, v); return { csrfToken: csrf(v, secret), isAdmin: false, telegram: true, name: v.telegramName, telegramAvailable: true };
  });
  app.post('/api/session/admin', { config: { rateLimit: { max: 8, timeWindow: '15 minutes' } } }, async (req, reply) => {
    const { password } = z.object({ password: z.string().max(256) }).strict().parse(req.body);
    if (!equal(password, config.adminPassword)) throw new ApiError(401, 'invalid_credentials', 'Неверный пароль.');
    const v = visitor(req, reply); v.adminUntil = Date.now() + 8 * 3600000; setVisitor(reply, v); return { ok: true };
  });
  app.delete('/api/session/admin', async (req, reply) => { const v = visitor(req, reply); delete v.adminUntil; setVisitor(reply, v); return { ok: true }; });
  app.get('/api/cities', async (_req, reply) => { reply.header('Cache-Control', 'public, max-age=60'); return references('cities'); });
  app.get('/api/categories', async (_req, reply) => { reply.header('Cache-Control', 'public, max-age=60'); return parser('/reference/categories'); });
  app.get('/api/venues', async (req, reply) => {
    const q = z.object({ cityId: uuid.optional() }).strict().parse(req.query); reply.header('Cache-Control', 'public, max-age=60');
    return references('venues' + (q.cityId ? `?cityId=${q.cityId}` : ''));
  });
  app.get('/api/events', async (req, reply) => {
    const filters = z.object({ q: z.string().max(300).optional(), city: z.string().max(200).optional(), category: z.string().max(30).optional(),
      from: z.iso.date().optional(), to: z.iso.date().optional(), maxPrice: z.coerce.number().min(0).max(9999999999).optional(),
      age: z.coerce.number().int().min(0).max(18).optional(), free: z.enum(['true']).optional(), venue: z.string().max(300).optional(),
      page: z.coerce.number().int().min(1).max(10000).default(1), sort: z.enum(['date_asc', 'price_asc', 'relevance']).default('date_asc'),
      pageSize: z.coerce.number().int().min(1).max(24).default(12) }).strict().parse(req.query);
    if (filters.from && filters.to && filters.from > filters.to) throw new ApiError(400, 'invalid_dates', 'Дата окончания должна быть не раньше начала.');
    const q = new URLSearchParams({ page: String(filters.page), page_size: String(filters.pageSize), statuses: 'Published',
      date_from: filters.from ? `${filters.from}T00:00:00+05:00` : new Date().toISOString(), verified_since: new Date(Date.now() - 24 * 3600000).toISOString(), sort: filters.sort });
    for (const status of ['Scheduled', 'Rescheduled', 'SoldOut']) q.append('source_statuses', status);
    if (filters.to) { const end = new Date(`${filters.to}T00:00:00+05:00`); end.setUTCDate(end.getUTCDate() + 1); q.set('date_to', end.toISOString()); }
    if (filters.category) q.set('categories', filters.category.replace(/^./, x => x.toUpperCase()));
    for (const [a, b] of [['q', 'q'], ['city', 'city'], ['venue', 'venue'], ['maxPrice', 'price_to'], ['age', 'age_limit_max'], ['free', 'is_free']] as const)
      if (filters[a] !== undefined && filters[a] !== '') q.set(b, String(filters[a]));
    if (filters.sort === 'relevance' && !filters.q) q.set('sort', 'date_asc');
    const data = await upstream(`/search/events?${q}`, 'public') as { items: ObjectData[]; total: number; page: number; pages: number };
    reply.header('Cache-Control', 'no-cache'); return { items: data.items.map(eventDto), total: data.total, page: data.page, pages: data.pages };
  });
  app.get('/api/events/:id', async (req, reply) => {
    const { id } = idParams.parse(req.params); const data = await upstream(`/search/events/${id}`, 'public') as ObjectData;
    if (data.status === 'Draft') throw new ApiError(404, 'not_found', 'Мероприятие не найдено.');
    const body = eventDto(data); const tag = '"' + createHash('sha256').update(JSON.stringify(body)).digest('hex') + '"';
    reply.header('ETag', tag).header('Cache-Control', 'no-cache');
    if (req.headers['if-none-match'] === tag) return reply.code(304).send();
    return body;
  });
  const mePaths = ['subscriptions', 'notifications', 'feedback'] as const;
  for (const resource of mePaths) app.get(`/api/me/${resource}`, async (req, reply) => {
    const v = visitor(req, reply); const q = z.object({ page: z.coerce.number().int().min(1).max(100000).default(1), pageSize: z.coerce.number().int().min(1).max(100).default(30), unreadOnly: z.enum(['true', 'false']).optional() }).strict().parse(req.query);
    return upstream(`/api/v1/interaction/me/${resource}?${new URLSearchParams(Object.entries(q).filter(([, x]) => x !== undefined).map(([k, x]) => [k, String(x)] as [string, string]))}`, 'user', { user: v });
  });
  app.post('/api/me/subscriptions', async (req, reply) => {
    const v = visitor(req, reply);
    const body = z.object({ cityId: uuid, category: z.string().max(30).nullable().optional(), channel: z.enum(['web', 'telegram']).default('web'),
      dateFrom: z.iso.date().nullable().optional(), dateTo: z.iso.date().nullable().optional(), consentConfirmed: z.literal(true) }).strict().parse(req.body);
    if (body.channel === 'telegram' && !v.telegram) throw new ApiError(400, 'telegram_identity_required', 'Для Telegram-подписки откройте приложение через бота.');
    return upstream('/api/v1/interaction/me/subscriptions', 'user', { method: 'POST', body, user: v });
  });
  app.delete('/api/me/subscriptions/:id', async (req, reply) => { const { id } = idParams.parse(req.params); await upstream(`/api/v1/interaction/me/subscriptions/${id}`, 'user', { method: 'DELETE', user: visitor(req, reply) }); return reply.code(204).send(); });
  app.delete('/api/me/subscriptions', async (req, reply) => { await upstream('/api/v1/interaction/me/subscriptions', 'user', { method: 'DELETE', user: visitor(req, reply) }); return reply.code(204).send(); });
  app.patch('/api/me/notifications/:id/read', async (req, reply) => { const { id } = idParams.parse(req.params); await upstream(`/api/v1/interaction/me/notifications/${id}/read`, 'user', { method: 'PATCH', user: visitor(req, reply) }); return reply.code(204).send(); });
  app.post('/api/me/feedback', { config: { rateLimit: { max: 5, timeWindow: '1 minute' } } }, async (req, reply) => {
    const body = z.object({ kind: z.enum(['question', 'correction', 'organizerApplication']), subject: z.string().trim().min(1).max(200),
      message: z.string().trim().min(1).max(5000), eventId: uuid.nullable().optional(), website: z.string().max(200).optional() }).strict().parse(req.body);
    const key = uuid.parse(req.headers['idempotency-key']);
    return upstream('/api/v1/interaction/me/feedback', 'user', { method: 'POST', body, key, user: visitor(req, reply) });
  });
  // Every admin route is guarded before parsing its body; the browser never receives service credentials.
  app.get('/api/admin/events', async req => {
    const q = z.object({ page: z.coerce.number().int().min(1).default(1), status: z.enum(['draft', 'published', 'cancelled']).optional() }).strict().parse(req.query);
    return parser(`/events?page=${q.page}&pageSize=20${q.status ? '&status=' + q.status : ''}`);
  });
  app.get('/api/admin/events/:id/audit', async req => { const { id } = idParams.parse(req.params); return parser(`/events/${id}/audit`); });
  app.patch('/api/admin/events/:id/review', async req => { const { id } = idParams.parse(req.params); return parser(`/events/${id}/review`, { method: 'PATCH', body: req.body }); });
  app.post('/api/admin/events', async req => parser('/events', { method: 'POST', body: req.body, key: uuid.parse(req.headers['idempotency-key']) }));
  app.post('/api/admin/imports/:format/:dataset', async req => {
    const { format, dataset } = z.object({ format: z.enum(['json', 'csv']), dataset: z.string().regex(/^[a-z][a-z0-9_-]{0,63}$/) }).parse(req.params);
    return parser(`/imports/${format}/${dataset}`, { method: 'POST', body: format === 'json' ? req.body : String(req.body), raw: format === 'csv', contentType: format === 'csv' ? 'text/csv' : 'application/json' });
  });
  for (const resource of ['sources', 'runs', 'stats']) app.get(`/api/admin/${resource}`, async () => parser(`/${resource}`));
  app.post('/api/admin/runs/:source', async req => {
    const { source } = z.object({ source: z.string().regex(/^[a-z][a-z0-9_-]{0,63}$/) }).parse(req.params);
    return parser(`/runs/${source}?maxPages=10`, { method: 'POST' });
  });
  app.delete('/api/admin/runs/:id', async req => { const { id } = idParams.parse(req.params); return parser(`/runs/${id}`, { method: 'DELETE' }); });
  app.get('/api/admin/feedback', async req => {
    const { page } = z.object({ page: z.coerce.number().int().min(1).default(1) }).strict().parse(req.query);
    return upstream(`/api/v1/interaction/admin/feedback?page=${page}&pageSize=30`, 'admin');
  });
  app.patch('/api/admin/feedback/:id/review', async req => { const { id } = idParams.parse(req.params); return upstream(`/api/v1/interaction/admin/feedback/${id}/review`, 'admin', { method: 'PATCH', body: req.body }); });
  app.post('/api/admin/reference/:kind', async req => {
    const { kind } = z.object({ kind: z.enum(['cities', 'venues']) }).parse(req.params);
    return parser(`/reference/${kind}`, { method: 'POST', body: req.body });
  });
  app.post('/api/admin/reference/venues/:id/aliases', async req => { const { id } = idParams.parse(req.params); return parser(`/reference/venues/${id}/aliases`, { method: 'POST', body: req.body }); });
  app.post('/api/admin/reference/normalize', async () => parser('/reference/normalize?page=1&pageSize=100', { method: 'POST' }));
  app.get('/health', async () => ({ status: 'healthy' }));
  const dist = resolve(projectRoot, 'dist');
  if (existsSync(dist)) {
    await app.register(staticFiles, { root: dist, prefix: '/', cacheControl: false });
    app.setNotFoundHandler((req, reply) => {
      if (req.url.startsWith('/api/') || req.method !== 'GET' || /\.[a-z0-9]+(?:\?|$)/i.test(req.url)) return reply.code(404).send({ title: 'Страница не найдена.' });
      reply.header('Cache-Control', 'no-cache'); return reply.sendFile('index.html');
    });
  }
  return app;
}
