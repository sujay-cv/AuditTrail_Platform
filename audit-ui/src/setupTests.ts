import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'

// jsdom has no layout: give elements a size so the virtualized table renders rows in tests.
Object.defineProperties(HTMLElement.prototype, {
  offsetHeight: { configurable: true, get: () => 600 },
  offsetWidth: { configurable: true, get: () => 1000 },
  clientHeight: { configurable: true, get: () => 600 },
  clientWidth: { configurable: true, get: () => 1000 },
})
HTMLElement.prototype.getBoundingClientRect = function () {
  return { x: 0, y: 0, top: 0, left: 0, bottom: 600, right: 1000, width: 1000, height: 600, toJSON: () => ({}) } as DOMRect
}

afterEach(() => {
  cleanup()
  sessionStorage.clear()
})
