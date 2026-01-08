import { describe, it, expect } from 'vitest'
import { useStore, CallState } from '../store/useStore'

describe('useStore', () => {
  it('should have initial state', () => {
    const state = useStore.getState()
    expect(state.callState).toBe(CallState.Idle)
    expect(state.dialedNumber).toBe('')
    expect(state.incomingNumber).toBeNull()
  })

  it('should update callState', () => {
    useStore.getState().setCallState(CallState.Ringing)
    expect(useStore.getState().callState).toBe(CallState.Ringing)
  })

  it('should update dialedNumber', () => {
    useStore.getState().setDialedNumber('123')
    expect(useStore.getState().dialedNumber).toBe('123')
  })
})
