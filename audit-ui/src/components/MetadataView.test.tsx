import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MetadataView } from './MetadataView'

const malicious = {
  comment: '<script>window.pwned()</script>',
  html: '<img src=x onerror="window.pwned()">',
  password: '[REDACTED]',
  nested: { cardNumber: '[REDACTED]', safe: 'visible' },
}

describe('MetadataView', () => {
  it('renders HTML / script payloads as plain text in BOTH the list and the JSON view', async () => {
    let executed = false
    ;(window as unknown as { pwned: () => void }).pwned = () => { executed = true }

    const { container } = render(<MetadataView metadata={malicious} redactedKeys={['password', 'nested.cardNumber']} />)
    expect(screen.getByLabelText('Event metadata')).toHaveTextContent('<script>window.pwned()</script>')

    await userEvent.click(screen.getByRole('button', { name: 'Show raw JSON' }))
    expect(screen.getByLabelText('Event metadata (JSON)')).toHaveTextContent('<script>window.pwned()</script>')

    expect(container.querySelector('script')).toBeNull()
    expect(container.querySelector('img')).toBeNull()
    expect(executed).toBe(false)
  })

  it('shows readable labels and clearly marks hidden (redacted) values', () => {
    render(<MetadataView metadata={malicious} redactedKeys={['password', 'nested.cardNumber']} />)
    expect(screen.getByText('2 values hidden for privacy', { exact: false })).toBeInTheDocument()
    expect(screen.getByText('Nested › Card number')).toBeInTheDocument()
    expect(screen.getAllByText('🔒 Hidden for privacy')).toHaveLength(2)
    expect(screen.getByText('visible')).toBeInTheDocument()
  })

  it('says so when there are no additional details', () => {
    render(<MetadataView metadata={{}} redactedKeys={[]} />)
    expect(screen.getByText('No additional details were recorded.')).toBeInTheDocument()
  })
})
