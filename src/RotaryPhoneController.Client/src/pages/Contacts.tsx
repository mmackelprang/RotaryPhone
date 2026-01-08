import { useEffect, useState } from 'react';
import { 
  Box, Button, Dialog, DialogTitle, DialogContent, 
  DialogActions, TextField, IconButton, Typography, Stack 
} from '@mui/material';
import { DataGrid, type GridColDef, type GridRenderCellParams } from '@mui/x-data-grid';
import { Add, Delete, Edit } from '@mui/icons-material';
import api from '../services/api';
import type { Contact } from '../types';

const Contacts: React.FC = () => {
  const [contacts, setContacts] = useState<Contact[]>([]);
  const [loading, setLoading] = useState(false);
  const [openDialog, setOpenDialog] = useState(false);
  const [currentContact, setCurrentContact] = useState<Partial<Contact>>({});

  const fetchContacts = async () => {
    setLoading(true);
    try {
      const response = await api.get<Contact[]>('/contacts');
      setContacts(response.data);
    } catch (error) {
      console.error('Failed to fetch contacts', error);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    fetchContacts();
  }, []);

  const handleSave = async () => {
    try {
      if (currentContact.id) {
        await api.put(`/contacts/${currentContact.id}`, currentContact);
      } else {
        await api.post('/contacts', currentContact);
      }
      setOpenDialog(false);
      setCurrentContact({});
      fetchContacts();
    } catch (error) {
      console.error('Failed to save contact', error);
    }
  };

  const handleDelete = async (id: string) => {
    if (!confirm('Are you sure you want to delete this contact?')) return;
    try {
      await api.delete(`/contacts/${id}`);
      fetchContacts();
    } catch (error) {
      console.error('Failed to delete contact', error);
    }
  };

  const openAdd = () => {
    setCurrentContact({});
    setOpenDialog(true);
  };

  const openEdit = (contact: Contact) => {
    setCurrentContact(contact);
    setOpenDialog(true);
  };

  const columns: GridColDef[] = [
    { field: 'name', headerName: 'Name', flex: 1 },
    { field: 'phoneNumber', headerName: 'Phone Number', flex: 1 },
    { field: 'email', headerName: 'Email', flex: 1 },
    {
      field: 'actions',
      headerName: 'Actions',
      width: 120,
      renderCell: (params: GridRenderCellParams<Contact>) => (
        <Stack direction="row">
          <IconButton size="small" onClick={() => openEdit(params.row)} color="primary">
            <Edit fontSize="small" />
          </IconButton>
          <IconButton size="small" onClick={() => handleDelete(params.row.id)} color="error">
            <Delete fontSize="small" />
          </IconButton>
        </Stack>
      ),
    },
  ];

  return (
    <Box sx={{ height: 600, width: '100%' }}>
      <Stack direction="row" justifyContent="space-between" alignItems="center" mb={2}>
        <Typography variant="h5">Contacts</Typography>
        <Button variant="contained" startIcon={<Add />} onClick={openAdd}>
          Add Contact
        </Button>
      </Stack>

      <DataGrid
        rows={contacts}
        columns={columns}
        loading={loading}
        disableRowSelectionOnClick
        sx={{ border: '1px solid #333' }}
      />

      <Dialog open={openDialog} onClose={() => setOpenDialog(false)}>
        <DialogTitle>{currentContact.id ? 'Edit Contact' : 'New Contact'}</DialogTitle>
        <DialogContent>
          <Stack spacing={2} sx={{ mt: 1, minWidth: 300 }}>
            <TextField
              label="Name"
              value={currentContact.name || ''}
              onChange={(e) => setCurrentContact({ ...currentContact, name: e.target.value })}
              fullWidth
              autoFocus
            />
            <TextField
              label="Phone Number"
              value={currentContact.phoneNumber || ''}
              onChange={(e) => setCurrentContact({ ...currentContact, phoneNumber: e.target.value })}
              fullWidth
            />
            <TextField
              label="Email"
              value={currentContact.email || ''}
              onChange={(e) => setCurrentContact({ ...currentContact, email: e.target.value })}
              fullWidth
            />
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setOpenDialog(false)}>Cancel</Button>
          <Button onClick={handleSave} variant="contained">Save</Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default Contacts;
