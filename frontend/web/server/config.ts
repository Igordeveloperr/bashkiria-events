import { readFileSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import { parse } from 'dotenv';

export const projectRoot = fileURLToPath(new URL('../', import.meta.url));
const read = (path: string): Record<string, string> => existsSync(path) ? parse(readFileSync(path)) : {};
const root = read(resolve(projectRoot, '../../.env'));
const local = read(resolve(projectRoot, '.env'));
const env = { ...root, ...local, ...process.env };
export const config = {
  gateway: env.GATEWAY_URL ?? 'http://127.0.0.1:8080',
  publicOrigin: env.PUBLIC_ORIGIN ?? 'http://localhost:5173',
  port: Number(env.PORT ?? 5174), host: env.HOST ?? '127.0.0.1',
  parserKey: env.PARSER_API_KEY ?? '', userKey: env.INTERACTION_API_KEY ?? '',
  adminKey: env.INTERACTION_ADMIN_API_KEY ?? '',
  adminPassword: env.WEB_ADMIN_PASSWORD ?? '',
  botToken: env.TELEGRAM_BOT_TOKEN ?? '', production: env.NODE_ENV === 'production',
  sessionSecret: env.APP_SESSION_SECRET ?? '',
};
export function validateConfig(): Buffer {
  if (!/^[a-f0-9]{64}$/i.test(config.sessionSecret)) throw new Error('APP_SESSION_SECRET must contain 64 random hex characters.');
  if (!config.parserKey || !config.userKey || !config.adminKey) throw new Error('Configure the service keys on the server.');
  if (config.adminPassword.length < 16) throw new Error('WEB_ADMIN_PASSWORD must contain at least 16 characters.');
  const gateway = new URL(config.gateway);
  if (!['http:', 'https:'].includes(gateway.protocol)) throw new Error('Invalid GATEWAY_URL.');
  return Buffer.from(config.sessionSecret, 'hex');
}
