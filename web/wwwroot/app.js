const messagesEl = document.getElementById('messages');
const inputEl = document.getElementById('input');
const sendBtn = document.getElementById('send');
const statusEl = document.getElementById('status');
const lockToggle = document.getElementById('lockToggle');
const panicClear = document.getElementById('panicClear');
const lockSession = document.getElementById('lockSession');

const errorMessages = {
  unauthorized: '認証が必要です。',
  empty: '入力が空です。',
  too_long: '入力が長すぎます。',
  cooldown: 'クールダウン中です。',
  daily_limit: '本日の上限に達しました。',
  llm_unreachable: 'LLMに接続できません。',
  llm_error: 'LLMでエラーが発生しました。',
  llm_invalid: 'LLMの応答が不正です。',
  llm_failed: 'LLMで失敗しました。',
  rate_limited: 'レート制限です。',
  timeout: 'タイムアウトです。',
  upstream_error: '上流エラーです。',
  failed: '失敗しました。'
};

const escapeHtml = (value) =>
  value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');

const escapeAttribute = (value) =>
  value.replace(/"/g, '&quot;').replace(/'/g, '&#39;');

const sanitizeUrl = (value) => {
  const url = value.trim();
  if (/^https?:\/\//i.test(url)) {
    return url;
  }
  return '#';
};

const renderInlineMarkdown = (value) => {
  const codePlaceholders = [];
  let html = escapeHtml(value).replace(/`([^`\n]+)`/g, (_, code) => {
    const placeholder = `@@INLINE_CODE_${codePlaceholders.length}@@`;
    codePlaceholders.push(`<code>${code}</code>`);
    return placeholder;
  });

  html = html.replace(/\[([^\]]+)\]\(([^)]+)\)/g, (_, label, url) => {
    const safeUrl = escapeAttribute(sanitizeUrl(url));
    return `<a href="${safeUrl}" target="_blank" rel="noopener noreferrer">${label}</a>`;
  });
  html = html.replace(/\*\*([^*\n]+)\*\*/g, '<strong>$1</strong>');
  html = html.replace(/\*([^*\n]+)\*/g, '<em>$1</em>');

  return html.replace(/@@INLINE_CODE_(\d+)@@/g, (_, index) => codePlaceholders[Number(index)] || '');
};

const renderMarkdown = (value) => {
  const lines = value.replace(/\r\n/g, '\n').split('\n');
  const html = [];
  let inList = false;
  let inCodeBlock = false;
  let codeLines = [];

  const closeList = () => {
    if (inList) {
      html.push('</ul>');
      inList = false;
    }
  };

  for (const line of lines) {
    if (line.startsWith('```')) {
      closeList();
      if (inCodeBlock) {
        html.push(`<pre><code>${escapeHtml(codeLines.join('\n'))}</code></pre>`);
        codeLines = [];
        inCodeBlock = false;
      } else {
        inCodeBlock = true;
      }
      continue;
    }

    if (inCodeBlock) {
      codeLines.push(line);
      continue;
    }

    const trimmed = line.trim();
    if (!trimmed) {
      closeList();
      continue;
    }

    const headingMatch = trimmed.match(/^(#{1,6})\s+(.+)$/);
    if (headingMatch) {
      closeList();
      const level = headingMatch[1].length;
      html.push(`<h${level}>${renderInlineMarkdown(headingMatch[2])}</h${level}>`);
      continue;
    }

    const listMatch = line.match(/^\s*[-*]\s+(.+)$/);
    if (listMatch) {
      if (!inList) {
        html.push('<ul>');
        inList = true;
      }
      html.push(`<li>${renderInlineMarkdown(listMatch[1])}</li>`);
      continue;
    }

    closeList();

    const quoteMatch = line.match(/^\s*>\s?(.*)$/);
    if (quoteMatch) {
      html.push(`<blockquote>${renderInlineMarkdown(quoteMatch[1])}</blockquote>`);
      continue;
    }

    html.push(`<p>${renderInlineMarkdown(line)}</p>`);
  }

  closeList();

  if (inCodeBlock) {
    html.push(`<pre><code>${escapeHtml(codeLines.join('\n'))}</code></pre>`);
  }

  return html.join('');
};

const appendMessage = (role, text) => {
  const wrapper = document.createElement('div');
  wrapper.className = `message ${role}`;
  if (role === 'assistant') {
    wrapper.classList.add('markdown');
    wrapper.innerHTML = renderMarkdown(text);
  } else {
    wrapper.textContent = text;
  }
  messagesEl.appendChild(wrapper);
  messagesEl.scrollTop = messagesEl.scrollHeight;
};

const setStatus = (text) => {
  statusEl.textContent = text;
};

const resolveErrorMessage = (codeOrMessage, status) => {
  if (!codeOrMessage && status) {
    return `エラー: ${status}`;
  }
  return errorMessages[codeOrMessage] || codeOrMessage || '不明なエラーです。';
};

const sendMessage = async () => {
  const text = inputEl.value.trim();
  if (!text) {
    return;
  }

  appendMessage('user', text);
  inputEl.value = '';
  setStatus('送信中...');

  try {
    const response = await fetch('/api/chat', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ input: text })
    });

    if (!response.ok) {
      const error = await response.json().catch(() => ({}));
      appendMessage('system', resolveErrorMessage(error.error, response.status));
      setStatus('');
      return;
    }

    const data = await response.json();
    appendMessage('assistant', data.text || '');
    if (data.model_used) {
      setStatus(`使用モデル: ${data.model_used}`);
    } else {
      setStatus('');
    }
  } catch (err) {
    appendMessage('system', 'ネットワークエラーです。');
    setStatus('');
  }
};

sendBtn.addEventListener('click', sendMessage);
inputEl.addEventListener('keydown', (event) => {
  if (event.key === 'Enter' && !event.shiftKey) {
    event.preventDefault();
    sendMessage();
  }
});

lockToggle.addEventListener('click', () => {
  messagesEl.classList.toggle('blurred');
});

panicClear.addEventListener('click', () => {
  messagesEl.innerHTML = '';
  setStatus('画面をクリアしました。');
});

lockSession.addEventListener('click', async () => {
  await fetch('/api/lock', { method: 'POST' });
  window.location.href = '/login';
});
