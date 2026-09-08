/* The one thing the sign-in screen does without the server: show the password. Everything else on
   this page is a form post, so there is nothing here to keep in sync with the server's idea of it. */
(function () {
  var pw = document.getElementById('password');
  var reveal = document.getElementById('reveal');
  reveal.addEventListener('click', function () {
    var shown = pw.type === 'text';
    pw.type = shown ? 'password' : 'text';
    reveal.textContent = shown ? 'show' : 'hide';
  });
})();
