Console top navigation. Active item gets the gold underline. Pass `markSrc` pointing at `assets/cryptosmith-mark.svg`.

```jsx
<TopNav items={['Overview','Strategies','Positions','Markets','Settings']} active="Overview" user="d.bykovas" markSrc="assets/cryptosmith-mark.svg" />
```

Note: the account chip's coin avatar uses a relative path; override with your own img if the page lives elsewhere.
