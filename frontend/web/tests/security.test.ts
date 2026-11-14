import { test } from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes, createHmac } from 'node:crypto';
import { csrf, decodeSession, encodeSession, newVisitor, validateTelegram } from '../server/security.ts';
import { createServer } from '../server/app.ts';

test('session rejects tampering and expiration, CSRF is bound to visitor', () => {
  const key = randomBytes(32), visitor = newVisitor(), cookie = encodeSession(visitor, key);
  assert.deepEqual(decodeSession(cookie, key), visitor);
  assert.equal(decodeSession(cookie.slice(0, -8) + 'AAAAAAAA', key), null);
  assert.equal(decodeSession(cookie, key, Date.now() + 91 * 86400000), null);
  assert.notEqual(csrf(visitor, key), csrf(newVisitor(), key));
});
test('Telegram signature and timestamp are verified on server', () => {
  const token = 'fixture:secret'; const params = new URLSearchParams({ auth_date: String(Math.floor(Date.now()/1000)), user: JSON.stringify({ id: 42, first_name: 'Тест' }) });
  const check = [...params].sort(([a], [b]) => a < b ? -1 : 1).map(([k,v]) => `${k}=${v}`).join('\n');
  params.set('hash', createHmac('sha256', createHmac('sha256', 'WebAppData').update(token).digest()).update(check).digest('hex'));
  assert.equal(validateTelegram(params.toString(), token).id, 'telegram:42');
  assert.throws(() => validateTelegram(params.toString(), 'wrong'));
  assert.throws(() => validateTelegram(params.toString(), token, Date.now()+600000));
});
test('BFF blocks anonymous admin and CSRF, sets HTTP-only session', async () => {
  const app = await createServer();
  try {
    assert.equal((await app.inject('/api/admin/events')).statusCode, 401);
    assert.equal((await app.inject({ method: 'POST', url:'/api/chat', payload:{message:'test'} })).statusCode, 403);
    const response = await app.inject('/api/session');
    assert.equal(response.statusCode, 200);
    assert.match(String(response.headers['set-cookie']), /HttpOnly/i);
    assert.ok(response.json().csrfToken);
    assert.equal(response.json().userKey, undefined);
  } finally { await app.close(); }
});
test('chat derives identity from cookie and forwards consent and stable request ID', async () => {
  const app = await createServer(); const original = globalThis.fetch; let forwarded: Record<string, unknown> = {};
  try {
    const session = await app.inject('/api/session');
    const headers = { cookie: String(session.headers['set-cookie']).split(';')[0], 'x-csrf-token': session.json().csrfToken };
    globalThis.fetch = async (_input, init) => { forwarded = JSON.parse(String(init?.body)); return new Response(JSON.stringify({reply:'Ответ',request_id:forwarded.request_id,trace_id:'trace'}),{status:200}); };
    const payload = { sessionId:'89e94be3-2f82-40f2-9e19-269bd1cac714', requestId:'fd547ff4-c2c5-49c4-a995-aa95863614ab', message:'Что есть в Уфе?',subscriptionConsent:false };
    assert.equal((await app.inject({method:'POST',url:'/api/chat',headers,payload:{...payload,user_id:'attacker'}})).statusCode,400);
    const response = await app.inject({method:'POST',url:'/api/chat',headers,payload});
    assert.equal(response.statusCode,200); assert.match(String(forwarded.user_id),/^web:/);
    assert.equal(forwarded.request_id,payload.requestId); assert.equal(forwarded.subscription_consent,false);
    assert.equal(response.json().user_id,undefined);
  } finally { globalThis.fetch=original; await app.close(); }
});
