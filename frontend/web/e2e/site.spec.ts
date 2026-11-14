import { test, expect } from '@playwright/test';
test('catalog, shareable filters and event permalink', async ({ page }) => {
  const failures: string[] = []; page.on('pageerror', e => failures.push(e.message));
  await page.goto('/'); await expect(page.getByRole('heading', { name: 'Афиша событий' })).toBeVisible();
  await expect(page.locator('.skeleton-card')).toHaveCount(0);
  await page.screenshot({path:'../artifacts/desktop.png',fullPage:true});
  await page.getByRole('button',{name:'Фильтры',exact:false}).click();
  await expect(page.getByPlaceholder('Название площадки')).toBeVisible();
  await page.getByRole('button',{name:'Завтра',exact:true}).click(); await expect(page).toHaveURL(/day=tomorrow/);
  await page.goto('/'); await expect(page.locator('.skeleton-card')).toHaveCount(0);
  const card=page.locator('.event-card').first();
  if(await card.count()) { await card.locator('h3 a').click(); await expect(page).toHaveURL(/\/events\//); await page.reload(); await expect(page.locator('h1')).toBeVisible(); }
  expect(failures).toEqual([]);
});
test('mobile 360px and widget without cookies',async({page,context})=>{
  await page.setViewportSize({width:360,height:800}); await page.goto('/');
  await expect(page.locator('.skeleton-card')).toHaveCount(0);
  expect(await page.evaluate(()=>document.documentElement.scrollWidth)).toBeLessThanOrEqual(360);
  await page.screenshot({path:'../artifacts/mobile.png',fullPage:true});
  await context.clearCookies(); await page.goto('/widget?day=week'); await expect(page.locator('#catalog-title')).toBeVisible();
  expect((await context.cookies()).filter(c=>c.name==='be_session')).toHaveLength(0);
});
test('assistant forwards stable message and offers fallback on failure',async({page})=>{
  const requests: Record<string,unknown>[]=[];
  await page.route('**/api/chat',route=>{requests.push(route.request().postDataJSON()); return route.fulfill({status:503,json:{title:'Сервис временно недоступен',code:'upstream_unavailable'}});});
  await page.goto('/assistant'); await page.getByLabel('Ваш запрос', {exact:true}).fill('Куда пойти в Уфе?');
  await expect(page.getByLabel('Отправить запрос')).toBeEnabled(); await page.getByLabel('Отправить запрос').click();
  await expect(page.getByRole('alert')).toContainText('Сервис временно недоступен'); await page.getByRole('button',{name:'Повторить',exact:true}).click();
  await expect.poll(()=>requests.length).toBe(2); expect(requests[0].requestId).toBe(requests[1].requestId); expect(requests[0].subscriptionConsent).toBe(false);
  expect(requests[0].user_id).toBeUndefined();
});
test('admin is protected and unknown route is handled',async({page,request})=>{
  expect((await request.get('/api/admin/events')).status()).toBe(401);
  await page.goto('/admin'); await expect(page.getByLabel('Пароль')).toBeVisible();
  await page.goto('/does-not-exist'); await expect(page.getByText('404 · немного не туда')).toBeVisible();
});
