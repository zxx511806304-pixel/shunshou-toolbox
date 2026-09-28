const pageTitle = document.title;
const visibleText = document.body?.innerText || '';
if (document.querySelectorAll('*').length > 50000) throw new Error('网页结构过大，请打开单篇文章或单章阅读页。');
const source = document.documentElement;
const clone = document.cloneNode(true);
const originals = [...source.querySelectorAll('*')];
const copies = [...clone.documentElement.querySelectorAll('*')];
for (let i = originals.length - 1; i >= 0; i--) {
  const node = originals[i], style = getComputedStyle(node);
  if (style.display === 'none' || style.visibility === 'hidden' || node.hidden || node.getAttribute('aria-hidden') === 'true') copies[i]?.remove();
}
clone.querySelectorAll('script,style,noscript,nav,footer,header,button,input,select,textarea,iframe,form,[role="dialog"]').forEach(n => n.remove());
const selectors = ['.read-content', '.reader-content', '.contentbox', '.noveltext', '#chaptercontent', '#content', '#js_content', 'article'];
const candidates = selectors.flatMap(s => [...clone.querySelectorAll(s)]).filter(n => {
  const text = n.textContent || '';
  const links = [...n.querySelectorAll('a')].reduce((sum,a) => sum + (a.textContent || '').length, 0);
  return text.trim().length >= 150 && links / Math.max(1,text.length) < .25;
});
let title = pageTitle, node = candidates.sort((a,b) => b.textContent.length - a.textContent.length)[0], method = '正文区域';
if (!node) {
  const article = new Readability(clone, { charThreshold: 150, maxElemsToParse: 50000, serializer: el => el }).parse();
  if (!article) throw new Error('没有找到可读取的正文。请打开具体文章或章节，必要时先在网页中完成登录。');
  node = article.content; title = article.title || title; method = 'Mozilla Readability';
}
node.querySelectorAll('br').forEach(n => n.replaceWith('\n'));
node.querySelectorAll('p,div,section,h1,h2,h3,li,blockquote').forEach(n => n.append('\n\n'));
let text = (node.textContent || '').replace(/[\t\u00a0\u3000]+/g,' ').replace(/ *\n */g,'\n').replace(/\n{3,}/g,'\n\n').trim();
if (text.length < 120 || (text.length < 800 && /访问验证|安全验证|请完成验证|访问受限|登录后阅读|购买本章|订阅本章|登录后查看|请先登录|403 Forbidden|Access Denied/i.test(text + pageTitle)))
  throw new Error('当前页面只有提示或验证内容，没有完整正文。请先在网页中正常打开内容后再提取。');
if (text.length > 500000) throw new Error('单页正文过长，请选择单篇文章或单章页面。');
if (/[\uE000-\uF8FF]/.test(text)) throw new Error('网页使用了特殊字形，直接复制会乱码。请切换网页预览，用“识别当前画面”提取当前可见文字。');
const warnings = /订阅本章|购买本章|付费后阅读|剩余内容|登录后继续阅读/.test(visibleText) ? '当前页可能只有试读内容，请与网页核对。' : '';
return { title, text, url: location.href, method, warnings, paragraphs: text.split(/\n\n/).length };
