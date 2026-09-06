// CryptoSmith X — theme toggle. The cookie is the source of truth and is read
// server-side in _Layout, which is what keeps the 10 s meta refresh flash-free.
// localStorage is deliberately NOT used: the server cannot read it.
document.addEventListener('click', (e) => {
  if (!e.target.closest('[data-theme-toggle]')) return;
  const next = document.documentElement.dataset.theme === 'light' ? 'dark' : 'light';
  document.documentElement.dataset.theme = next;
  document.cookie = 'csx_theme=' + next + ';path=/;max-age=31536000;samesite=lax';
});
