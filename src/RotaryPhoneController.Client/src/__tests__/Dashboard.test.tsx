import { describe, it, expect, vi } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import Dashboard from '../pages/Dashboard'
import { useStore, CallState } from '../store/useStore'
import api from '../services/api'

// Mock API
vi.mock('../services/api', () => ({
  default: {
    post: vi.fn()
  }
}))

describe('Dashboard Component', () => {
  it('renders status correctly', () => {
    // Set initial state
    useStore.setState({ callState: CallState.Idle, dialedNumber: '' })
    
    render(<Dashboard />)
    
    expect(screen.getByText('IDLE')).toBeInTheDocument()
  })

  it('renders ringing state with incoming number', () => {
    useStore.setState({ 
      callState: CallState.Ringing, 
      incomingNumber: '555-1234' 
    })
    
    render(<Dashboard />)
    
    expect(screen.getByText('RINGING')).toBeInTheDocument()
    expect(screen.getByText(/Incoming Call: 555-1234/)).toBeInTheDocument()
  })

  it('calls simulateLiftHandset API on button click', async () => {
    useStore.setState({ callState: CallState.Idle })
    render(<Dashboard />)
    
    const liftBtn = screen.getByText('LIFT HANDSET')
    fireEvent.click(liftBtn)
    
    expect(api.post).toHaveBeenCalledWith('/phone/simulate/hook?offHook=true')
  })

  it('calls simulateDial API on dial button click', async () => {
    useStore.setState({ callState: CallState.Dialing })
    render(<Dashboard />)
    
    const input = screen.getByRole('textbox')
    fireEvent.change(input, { target: { value: '123' } })
    
    const dialBtn = screen.getByText('DIAL')
    fireEvent.click(dialBtn)
    
    expect(api.post).toHaveBeenCalledWith('/phone/simulate/dial?digits=123')
  })
})
