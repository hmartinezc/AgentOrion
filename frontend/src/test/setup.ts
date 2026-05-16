import '@testing-library/jest-dom'

Object.defineProperty(window.HTMLElement.prototype, 'scrollIntoView', {
  value: () => undefined,
  writable: true
})
