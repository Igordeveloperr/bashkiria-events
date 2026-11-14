import { createCipheriv, createDecipheriv, createHmac, randomBytes, randomUUID, timingSafeEqual } from 'node:crypto';

export interface Visitor { id: string; created: number; adminUntil?: number; telegramName?: string; telegram?: boolean }
const MAX_AGE = 90 * 24 * 60 * 60 * 1000;
export function equal(a: string, b: string): boolean {
  const x = createHmac('sha256', 'comparison').update(a).digest();
  const y = createHmac('sha256', 'comparison').update(b).digest();
  return timingSafeEqual(x, y);
}
export function newVisitor(now = Date.now()): Visitor { return { id: `web:${randomUUID()}`, created: now }; }
export function encodeSession(visitor: Visitor, key: Buffer): string {
  const iv = randomBytes(12); const cipher = createCipheriv('aes-256-gcm', key, iv);
  const payload = Buffer.concat([cipher.update(JSON.stringify(visitor)), cipher.final()]);
  return Buffer.concat([iv, cipher.getAuthTag(), payload]).toString('base64url');
}
export function decodeSession(cookie: string | undefined, key: Buffer, now = Date.now()): Visitor | null {
  if (!cookie || cookie.length > 2048) return null;
  try {
    const raw = Buffer.from(cookie, 'base64url');
    const cipher = createDecipheriv('aes-256-gcm', key, raw.subarray(0, 12)); cipher.setAuthTag(raw.subarray(12, 28));
    const value: unknown = JSON.parse(Buffer.concat([cipher.update(raw.subarray(28)), cipher.final()]).toString());
    if (!value || typeof value !== 'object') return null;
    const v = value as Visitor;
    return typeof v.id === 'string' && /^(web:[a-f0-9-]{36}|telegram:\d+)$/.test(v.id) &&
      typeof v.created === 'number' && v.created <= now && now - v.created < MAX_AGE ? v : null;
  } catch { return null; }
}
export function csrf(visitor: Visitor, key: Buffer): string { return createHmac('sha256', key).update(`csrf:${visitor.id}:${visitor.created}`).digest('hex'); }
export function validateTelegram(initData: string, botToken: string, now = Date.now()): Visitor {
  const params = new URLSearchParams(initData);
  if (initData.length > 16000 || [...params.keys()].length !== new Set(params.keys()).size) throw new Error('invalid_init_data');
  const hash = params.get('hash'); params.delete('hash');
  const check = [...params.entries()].sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0).map(([k, v]) => `${k}=${v}`).join('\n');
  const secret = createHmac('sha256', 'WebAppData').update(botToken).digest();
  const signature = createHmac('sha256', secret).update(check).digest('hex');
  const authDate = Number(params.get('auth_date')) * 1000;
  if (!hash || !/^[a-f0-9]{64}$/.test(hash) || !equal(signature, hash) || !Number.isFinite(authDate) ||
      authDate > now + 30000 || now - authDate > 5 * 60 * 1000) throw new Error('invalid_init_data');
  const user: unknown = JSON.parse(params.get('user') ?? '{}');
  if (!user || typeof user !== 'object' || !('id' in user) || typeof user.id !== 'number' || !Number.isSafeInteger(user.id) || user.id <= 0) throw new Error('invalid_init_data');
  const name = 'first_name' in user && typeof user.first_name === 'string' ? user.first_name.slice(0, 100) : 'Пользователь';
  return { id: `telegram:${user.id}`, created: now, telegram: true, telegramName: name };
}
